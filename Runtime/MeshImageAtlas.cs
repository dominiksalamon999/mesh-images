using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using static MeshImages.MeshImageAtlasUtility;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace MeshImages
{
    [DefaultExecutionOrder(-10000)]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public partial class MeshImageAtlas : MonoBehaviour
    {
        // ---------------- Constants & nested ----------------
        private const float MinFitDimension = 1e-5f;
        private const int AtlasSize = 1536;
        private const GraphicsFormat AtlasColorFormat = GraphicsFormat.R8G8B8A8_SRGB;
        private const GraphicsFormat AtlasDepthFormat = GraphicsFormat.D24_UNorm_S8_UInt;

        private struct AtlasEntry
        {
            public int Slot; public Mesh Mesh; public Material Material;
            public Vector3 Position, Rotation, Scale;
        }

        // ---------------- Inspector ----------------
        [Header("References")]
        [SerializeField] private Camera atlasCamera;
        [SerializeField] private Material defaultMaterial;

        [Header("Layout")]
        [Tooltip("Cells per row/column. Total slots = gridSize * gridSize.")]
        [SerializeField, Min(1)] private int gridSize = 7;
        [Tooltip("Object size within its cell, as a fraction of the cell.")]
        [SerializeField, Range(0.05f, 1f)] private float objectFillFraction = 0.4f;
        [Tooltip("UV padding inside each slot, as a fraction of slot size.")]
        [SerializeField, Range(0f, 0.25f)] private float uvPaddingFraction = 1f / 20f;
        [Tooltip("Local Z distance in front of the camera. Must sit between near and far clip planes.")]
        [SerializeField] private float previewDepth = 100f;

        [Header("Auto Layout")]
        [SerializeField] private bool autoFit = true;
        [SerializeField] private bool autoCenter = true;

        [Header("Render")]
        [Tooltip("Idle re-render rate cap (fps). Changes always render immediately. 0 = no idle re-renders.")]
        [SerializeField] private float renderFps = 5f;

        // ---------------- State ----------------
        private readonly Dictionary<MeshImage, AtlasEntry> _pairs = new();
        private readonly MeshImageSlotPool _pool = new();
        private int _resolvedPreviewLayer, _lastCullingMask = -1;
        private double _lastRenderTime;
        private bool _dirty = true, _autoSpawned;
        private RenderTexture atlasTexture;

        private static bool _isQuitting;
        private static MeshImageAtlas _instance;

        // ---------------- Properties ----------------
        public RenderTexture Texture => atlasTexture;
        public Camera Camera => atlasCamera;

        private float ViewHalfExtent => atlasCamera != null ? atlasCamera.orthographicSize : 0f;
        private float CellFillSize => objectFillFraction * (2f * ViewHalfExtent / gridSize);
        private int MaxSlots => gridSize * gridSize;
        private SlotLayoutConfig LayoutConfig => new(gridSize, uvPaddingFraction, previewDepth, ViewHalfExtent);

        // ---------------- Singleton ----------------
        public static MeshImageAtlas Instance
        {
            get
            {
                if (_isQuitting) return null;
                if (_instance) return _instance;
                _instance = FindFirstObjectByType<MeshImageAtlas>();
                if (_instance) return _instance;

#if UNITY_EDITOR
                // Don't spawn during unsafe editor contexts — would either
                // warn ("not cleaned up") or be destroyed milliseconds later.
                if (!Application.isPlaying &&
                    (EditorApplication.isCompiling || EditorApplication.isUpdating
                     || BuildPipeline.isBuildingPlayer
                     || EditorApplication.isPlayingOrWillChangePlaymode)) return null;
#else
                if (!Application.isPlaying) return null;
#endif

                var prefab = Resources.Load<MeshImageAtlas>("MeshImageAtlas");
                if (!prefab) { Debug.LogError("MeshImageAtlas prefab not found."); return null; }

                _instance = Instantiate(prefab);
                _instance.name = "[MeshImageAtlas]";
                _instance.gameObject.hideFlags = HideFlags.None;
                _instance._autoSpawned = true;
                return _instance;
            }
        }

        /// <summary>Non-spawning accessor for contexts where Instantiate is forbidden (OnValidate).</summary>
        internal static MeshImageAtlas ExistingInstance
        {
            get
            {
                if (_isQuitting) return null;
#if UNITY_EDITOR
                // Prefab-stage editing has its own isolated scene; prefer an
                // atlas in that scene over the main-scene singleton. The scene
                // can be unloaded mid-transition, so guard against that.
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null && stage.scene.IsValid() && stage.scene.isLoaded)
                    foreach (var root in stage.scene.GetRootGameObjects())
                    {
                        var found = root.GetComponentInChildren<MeshImageAtlas>(true);
                        if (found != null) return found;
                    }
#endif
                if (_instance) return _instance;
                _instance = FindFirstObjectByType<MeshImageAtlas>();
                return _instance;
            }
        }

        // ---------------- Public API ----------------
        public void Add(MeshImage image, Mesh mesh, Material material,
                        Vector3 position, Vector3 rotation, Vector3 scale)
            => TryAdd(image, mesh, material, position, rotation, scale);

        internal void Remove(MeshImage image)
        {
            if (image == null || !_pairs.TryGetValue(image, out var entry)) return;
            _pool.ReleaseSlot(entry.Slot);
            _pairs.Remove(image);
            MarkDirty();
        }

        // ---------------- Lifecycle ----------------
        private void OnEnable()
        {
            // Defensive: clear stale quit flag if domain-reload was disabled
            // and we missed the editor callback.
            _isQuitting = false;

            if (_instance != null && _instance != this) { DestroySafe(gameObject); return; }
            _instance = this;

            // If this instance was loaded from a prefab that had hideFlags
            // baked in (older dev iteration set DontSaveInEditor), clear them.
            // Cleanup safety is handled by the editor hooks for auto-spawned instances.
            if (gameObject.hideFlags != HideFlags.None) gameObject.hideFlags = HideFlags.None;
            if (Application.isPlaying) DontDestroyOnLoad(gameObject);

            if (atlasCamera == null) atlasCamera = GetComponent<Camera>();

            CreateAtlasTexture();
            atlasCamera.targetTexture = atlasTexture;
            // Lock aspect to the square RT so grid math holds if external code skewed it.
            atlasCamera.aspect = (float)atlasTexture.width / atlasTexture.height;
            atlasCamera.enabled = false; // we drive Render() manually

            _resolvedPreviewLayer = ResolvePreviewLayer();
            _lastCullingMask = atlasCamera.cullingMask;

            _pool.Build(transform, gridSize, _resolvedPreviewLayer);
            ValidateConfiguration();

            // Re-place registrations carried across disable/enable so
            // MeshImages aren't stranded on a released RT.
            ReplaceCarriedEntries();

#if UNITY_EDITOR
            if (!Application.isPlaying) ToggleEditorHooks(true);
#endif
        }

        private void OnDisable()
        {
            // Keep _pairs across disable so re-enabling can re-place every image.
            _pool.Destroy();
            ReleaseAtlasTexture();

#if UNITY_EDITOR
            ToggleEditorHooks(false);
            _editorRenderQueued = _deactivateQueued = false;
#endif
        }

        private void OnDestroy() { if (_instance == this) _instance = null; }
        private void OnApplicationQuit() => _isQuitting = true;

        private void OnValidate()
        {
            if (gridSize < 1) gridSize = 1;
            if (previewDepth <= 0f) previewDepth = 0.0001f;
            if (renderFps < 0f) renderFps = 0f;
            objectFillFraction = Mathf.Clamp(objectFillFraction, 0.05f, 1f);
            uvPaddingFraction = Mathf.Clamp(uvPaddingFraction, 0f, 0.25f);
            // Pool rebuild is deferred to EnsurePoolMatchesGrid().
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;
            EnsurePoolMatchesGrid();
            _pool.DrainPendingDeactivates();
            TryRender(Time.unscaledTimeAsDouble);
        }

        // ---------------- Add pipeline ----------------
        private bool TryAdd(MeshImage image, Mesh mesh, Material material, Vector3 position, Vector3 eulerRotation, Vector3 scale)
        {
            if (image == null || _pairs.ContainsKey(image)) return false;
            if (material == null) material = defaultMaterial;
            if (mesh == null || material == null) return false;

            EnsurePoolMatchesGrid();

            if (!_pool.TryAcquireSlot(out int slot))
            {
                Debug.LogWarning($"{nameof(MeshImageAtlas)} on '{name}': atlas is full " +
                                 $"({MaxSlots} slots); cannot place '{image.name}'. " +
                                 $"Increase gridSize or remove a MeshImage.", image);
                return false;
            }

            // Roll back slot acquisition on any failure before the final _pairs assignment.
            try
            {
                var rotation = Quaternion.Euler(eulerRotation);

                float fitScale = CellFillSize;
                if (autoFit)
                {
                    var rotated = ComputeRotatedScaledAabb(mesh, Vector3.one, rotation);
                    float maxXY = Mathf.Max(rotated.size.x, rotated.size.y);
                    if (maxXY > MinFitDimension) fitScale = CellFillSize / maxXY;
                }

                Vector3 finalScale = scale * fitScale;
                Vector3 centerOffset = Vector3.zero;
                if (autoCenter)
                {
                    var rotated = ComputeRotatedScaledAabb(mesh, finalScale, rotation);
                    centerOffset = new Vector3(-rotated.center.x, -rotated.center.y, 0f);
                }

                var cfg = LayoutConfig;
                var s = _pool[slot];
                s.Filter.sharedMesh = mesh;
                s.Renderer.sharedMaterial = material;

                var t = s.Go.transform;
                t.localScale = finalScale;
                t.localEulerAngles = eulerRotation;
                t.localPosition = MeshImageSlotPool.GetSlotLocalPosition(slot, position, in cfg) + centerOffset;
                s.Go.SetActive(true);

                image.SetUvFromAtlas(MeshImageSlotPool.GetSlotUvRect(slot, in cfg));
                image.texture = atlasTexture;

                _pairs[image] = new AtlasEntry
                {
                    Slot = slot,
                    Mesh = mesh,
                    Material = material,
                    Position = position,
                    Rotation = eulerRotation,
                    Scale = scale,
                };
                MarkDirty();
                return true;
            }
            catch
            {
                _pool.ReleaseSlot(slot);
                _pairs.Remove(image);
                throw;
            }
        }

        // ---------------- Pool coordination ----------------
        // Rebuild the pool if gridSize changed; re-place existing entries.
        private void EnsurePoolMatchesGrid()
        {
            if (!_pool.NeedsRebuild(gridSize)) return;
            _pool.Build(transform, gridSize, _resolvedPreviewLayer);
            ReplaceCarriedEntries();
        }

        // Snapshot current registrations, clear _pairs, re-add into the
        // (presumed freshly built) pool. Used after disable/enable and grid
        // resize. Logs an aggregate diagnostic if capacity dropped.
        private void ReplaceCarriedEntries()
        {
            if (_pairs.Count == 0) return;
            var carry = new List<KeyValuePair<MeshImage, AtlasEntry>>(_pairs);
            _pairs.Clear();

            int dropped = 0;
            for (int i = 0; i < carry.Count; i++)
            {
                var img = carry[i].Key;
                if (img == null) continue;
                var e = carry[i].Value;
                if (!TryAdd(img, e.Mesh, e.Material, e.Position, e.Rotation, e.Scale)) dropped++;
            }

            if (dropped > 0)
                Debug.LogWarning($"{nameof(MeshImageAtlas)} on '{name}': dropped {dropped} of " +
                                 $"{carry.Count} image(s) while rebuilding pool — capacity is " +
                                 $"{MaxSlots} slots. See per-image warnings above for names.", this);
        }

        // ---------------- Atlas texture ----------------
        private void CreateAtlasTexture()
        {
            if (atlasTexture != null) return;
            atlasTexture = new RenderTexture(AtlasSize, AtlasSize, AtlasColorFormat, AtlasDepthFormat)
            {
                name = $"{name}_AutoAtlas",
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave,
            };
            atlasTexture.Create();
        }

        private void ReleaseAtlasTexture()
        {
            if (atlasTexture == null) return;
            if (atlasCamera != null && atlasCamera.targetTexture == atlasTexture)
                atlasCamera.targetTexture = null;
            atlasTexture.Release();
            DestroySafe(atlasTexture);
            atlasTexture = null;
        }

        // ---------------- Render ----------------
        private void MarkDirty()
        {
            _dirty = true;
#if UNITY_EDITOR
            // OnEditorUpdate polls only every ~100 ms. Hop one editor-update
            // tick to render ASAP — far faster than EditorApplication.delayCall.
            // Camera.Render() is forbidden during OnValidate, so we can't inline.
            if (!Application.isPlaying) QueueEditorRender();
#endif
        }

        private void TryRender(double now)
        {
            if (atlasCamera == null || atlasTexture == null) return;

            if (atlasCamera.targetTexture != atlasTexture)
                atlasCamera.targetTexture = atlasTexture;
            // Defensive: re-lock aspect so external code can't skew the grid.
            atlasCamera.aspect = (float)atlasTexture.width / atlasTexture.height;
            RefreshPreviewLayerIfNeeded();

#if UNITY_EDITOR
            if (EditorApplication.isCompiling || EditorApplication.isUpdating
                || BuildPipeline.isBuildingPlayer) return;
#endif

            // Dirty renders are immediate; idle renders throttled by renderFps.
            if (!_dirty)
            {
                if (renderFps <= 0f) return;
                if (now - _lastRenderTime < 1.0 / renderFps) return;
            }
            _lastRenderTime = now;
            _dirty = false;
            RenderAtlasCamera();
        }

        private void RenderAtlasCamera()
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                var req = new RenderPipeline.StandardRequest { destination = atlasTexture };
                if (RenderPipeline.SupportsRenderRequest(atlasCamera, req))
                {
                    RenderPipeline.SubmitRenderRequest(atlasCamera, req);
                    return;
                }
                Debug.LogWarning("SRP does not support render requests.");
            }
            atlasCamera.Render();
        }

        // ---------------- Validation & layer ----------------
        private void ValidateConfiguration()
        {
            string prefix = $"{nameof(MeshImageAtlas)} on '{name}'";
            if (defaultMaterial == null)
                Debug.LogWarning($"{prefix}: no defaultMaterial assigned.", this);
            if (!atlasCamera.orthographic)
                Debug.LogWarning($"{prefix}: camera is not orthographic.", this);
            if (atlasCamera.cullingMask == 0)
                Debug.LogWarning($"{prefix}: camera culling mask is empty; slots will not render.", this);
            if (previewDepth < atlasCamera.nearClipPlane || previewDepth > atlasCamera.farClipPlane)
                Debug.LogWarning($"{prefix}: previewDepth ({previewDepth}) is outside camera clip range " +
                                 $"[{atlasCamera.nearClipPlane}, {atlasCamera.farClipPlane}]; " +
                                 $"slots will be clipped.", this);
        }

        // Pick the lowest layer in the camera's culling mask. If empty, return
        // 0 (Default) — using the camera's own layer would be guaranteed NOT
        // to be in an empty mask, producing a silent black atlas.
        private int ResolvePreviewLayer()
        {
            int mask = atlasCamera.cullingMask;
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) return i;
            return 0;
        }

        private void RefreshPreviewLayerIfNeeded()
        {
            if (atlasCamera == null || _pool.Capacity == 0) return;
            int mask = atlasCamera.cullingMask;
            if (mask == _lastCullingMask) return;
            _lastCullingMask = mask;

            int newLayer = ResolvePreviewLayer();
            if (newLayer == _resolvedPreviewLayer) return;
            _resolvedPreviewLayer = newLayer;

            _pool.SetPreviewLayer(newLayer);
        }
    }
}