using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
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
    public class MeshImageAtlas : MonoBehaviour
    {
        // ---------------- Constants & nested ----------------
        private const float MinFitDimension = 1e-5f;
        private const string SlotName = "MeshImage_Slot";
        private const int AtlasSize = 1536;
        private const GraphicsFormat AtlasColorFormat = GraphicsFormat.R8G8B8A8_SRGB;
        private const GraphicsFormat AtlasDepthFormat = GraphicsFormat.D24_UNorm_S8_UInt;

        private struct Slot { public GameObject Go; public MeshFilter Filter; public MeshRenderer Renderer; }
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
        private readonly Queue<int> _freeSlots = new();
        private readonly Queue<int> _pendingDeactivate = new();
        private Slot[] _slots;
        private int _nextSlot, _builtGridSize = -1, _resolvedPreviewLayer, _lastCullingMask = -1;
        private double _lastRenderTime;
        private bool _dirty = true, _autoSpawned, _srpFallbackWarned;
        private RenderTexture atlasTexture;

        private static MethodInfo _setActiveMethod;

        private static bool _isQuitting;
        private static MeshImageAtlas _instance;

#if UNITY_EDITOR
        private bool _editorRenderQueued, _deactivateQueued;
#endif

        // ---------------- Properties ----------------
        public RenderTexture Texture => atlasTexture;
        public Camera Camera => atlasCamera;

        private float ViewHalfExtent => atlasCamera != null ? atlasCamera.orthographicSize : 0f;
        private float CellSize => 2f * ViewHalfExtent / gridSize;
        private float CellFillSize => objectFillFraction * CellSize;
        private int MaxSlots => gridSize * gridSize;

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
            ClearSlot(entry.Slot);
            _freeSlots.Enqueue(entry.Slot);
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

            BuildSlotPool();
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
            DestroySlotPool();
            ReleaseAtlasTexture();

#if UNITY_EDITOR
            ToggleEditorHooks(false);
            _editorRenderQueued = _deactivateQueued = false;
            _pendingDeactivate.Clear();
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
            DrainPendingDeactivates();
            TryRender(Time.unscaledTimeAsDouble);
        }

        // ---------------- Add pipeline ----------------
        private bool TryAdd(MeshImage image, Mesh mesh, Material material,
                            Vector3 position, Vector3 eulerRotation, Vector3 scale)
        {
            if (image == null || _pairs.ContainsKey(image)) return false;
            if (material == null) material = defaultMaterial;
            if (mesh == null || material == null) return false;

            EnsurePoolMatchesGrid();

            if (!TryAcquireSlot(out int slot))
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

                var s = _slots[slot];
                s.Filter.sharedMesh = mesh;
                s.Renderer.sharedMaterial = material;

                var t = s.Go.transform;
                t.localScale = finalScale;
                t.localEulerAngles = eulerRotation;
                t.localPosition = GetSlotLocalPosition(slot, position) + centerOffset;
                s.Go.SetActive(true);

                image.SetUvFromAtlas(GetSlotUvRect(slot));
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
                ClearSlot(slot);
                _freeSlots.Enqueue(slot);
                _pairs.Remove(image);
                throw;
            }
        }

        // ---------------- Slot pool ----------------
        private void BuildSlotPool()
        {
            DestroySlotPool();
            _slots = new Slot[MaxSlots];

            for (int i = 0; i < _slots.Length; i++)
            {
                var go = new GameObject($"{SlotName}_{i}", typeof(MeshFilter), typeof(MeshRenderer))
                { hideFlags = HideFlags.HideAndDontSave, layer = _resolvedPreviewLayer };
                go.transform.SetParent(transform, false);
                go.SetActive(false);

                var mr = go.GetComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

                _slots[i] = new Slot { Go = go, Filter = go.GetComponent<MeshFilter>(), Renderer = mr };
            }

            _builtGridSize = gridSize;
            ResetSlotBookkeeping();
        }

        private void DestroySlotPool()
        {
            if (_slots != null)
                for (int i = 0; i < _slots.Length; i++)
                    if (_slots[i].Go != null) DestroySafe(_slots[i].Go);
            _slots = null;
            _builtGridSize = -1;
            ResetSlotBookkeeping();
        }

        private void ResetSlotBookkeeping()
        {
            _freeSlots.Clear();
            _pendingDeactivate.Clear();
            _nextSlot = 0;
        }

        private void ClearSlot(int slot)
        {
            if (_slots == null || slot < 0 || slot >= _slots.Length) return;
            var s = _slots[slot];
            if (s.Filter != null) s.Filter.sharedMesh = null;
            if (s.Renderer != null) s.Renderer.sharedMaterial = null;
            // SetActive(false) fires OnBecameInvisible, forbidden during
            // OnValidate/Awake/CheckConsistency. Defer it. If a subsequent
            // Add re-uses this slot, TryAdd's SetActive(true) wins and the
            // deferred deactivate is a no-op.
            if (s.Go != null) QueueDeactivate(slot);
        }

        private void QueueDeactivate(int slot)
        {
            _pendingDeactivate.Enqueue(slot);
#if UNITY_EDITOR
            if (!Application.isPlaying && !_deactivateQueued)
            {
                _deactivateQueued = true;
                EditorApplication.QueuePlayerLoopUpdate();
                EditorApplication.update += FlushPendingDeactivates;
            }
            // Play mode: LateUpdate drains the queue unconditionally.
#endif
        }

        private void DrainPendingDeactivates()
        {
            if (_slots == null) { _pendingDeactivate.Clear(); return; }

            while (_pendingDeactivate.Count > 0)
            {
                int slot = _pendingDeactivate.Dequeue();
                if (slot < 0 || slot >= _slots.Length) continue;
                var s = _slots[slot];
                if (s.Go == null) continue;
                // If the slot was re-acquired since deferral, leave it active.
                if (s.Filter != null && s.Filter.sharedMesh != null) continue;
                s.Go.SetActive(false);
            }
        }

        // Rebuild the pool if gridSize changed; re-place existing entries.
        private void EnsurePoolMatchesGrid()
        {
            if (_slots != null && _builtGridSize == gridSize) return;
            BuildSlotPool();
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

        // ---------------- Slot math ----------------
        private bool TryAcquireSlot(out int slot)
        {
            if (_freeSlots.Count > 0) { slot = _freeSlots.Dequeue(); return true; }
            if (_nextSlot >= MaxSlots) { slot = -1; return false; }
            slot = _nextSlot++;
            return true;
        }

        private Rect GetSlotUvRect(int slot)
        {
            float v = 1f / gridSize, pad = v * uvPaddingFraction;
            int col = slot % gridSize, row = slot / gridSize;
            return new Rect(col * v + pad, (1f - v) - row * v + pad, v - 2f * pad, v - 2f * pad);
        }

        private Vector3 GetSlotLocalPosition(int slot, Vector3 offset)
        {
            int col = slot % gridSize, row = slot / gridSize;
            float cell = CellSize, half = ViewHalfExtent;
            return new Vector3(-half + (col + 0.5f) * cell,
                               +half - (row + 0.5f) * cell,
                               previewDepth) + offset;
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
                var req = new RenderPipeline.StandardRequest
                {
                    destination = atlasTexture
                };

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
            if (mask == 0) return 0;
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) return i;
            return 0;
        }

        private void RefreshPreviewLayerIfNeeded()
        {
            if (atlasCamera == null || _slots == null) return;
            int mask = atlasCamera.cullingMask;
            if (mask == _lastCullingMask) return;
            _lastCullingMask = mask;

            int newLayer = ResolvePreviewLayer();
            if (newLayer == _resolvedPreviewLayer) return;
            _resolvedPreviewLayer = newLayer;

            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].Go != null) _slots[i].Go.layer = newLayer;
        }

        // ---------------- Helpers ----------------
        private static Bounds ComputeRotatedScaledAabb(Mesh mesh, Vector3 scale, Quaternion rotation)
        {
            var b = mesh.bounds;
            Vector3 c = b.center, e = b.extents;
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;

            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + (((i & 1) == 0) ? +e.x : -e.x),
                    c.y + (((i & 2) == 0) ? +e.y : -e.y),
                    c.z + (((i & 4) == 0) ? +e.z : -e.z));
                var transformed = rotation * Vector3.Scale(corner, scale);
                min = Vector3.Min(min, transformed);
                max = Vector3.Max(max, transformed);
            }

            var result = new Bounds();
            result.SetMinMax(min, max);
            return result;
        }

        private static void DestroySafe(Object obj)
        {
            if (obj == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying) { DestroyImmediate(obj); return; }
#endif
            Destroy(obj);
        }

        // ---------------- Editor only ----------------
#if UNITY_EDITOR
        // OnApplicationQuit sets _isQuitting on every play-mode exit. Without
        // resetting it, the static survives the play→edit transition (no
        // domain reload by default) and Instance returns null for the rest of
        // the edit session — silently breaking every MeshImage.
        [InitializeOnLoadMethod]
        private static void EditorInit()
        {
            _isQuitting = false;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode) _isQuitting = true;
            else if (state == PlayModeStateChange.EnteredEditMode) _isQuitting = false;
        }

        // Single sub/unsub point. on=true subscribes (after a defensive unsub);
        // on=false just unsubscribes everything this instance attached.
        private void ToggleEditorHooks(bool on)
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update -= EditorRenderOnce;
            EditorApplication.update -= FlushPendingDeactivates;
            EditorSceneManager.sceneOpened -= OnEditorSceneOpened;
            EditorSceneManager.sceneClosing -= OnEditorSceneClosing;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChangedInstance;
            if (!on) return;

            EditorApplication.update += OnEditorUpdate;
            EditorApplication.delayCall += EditorRegisterAllImages;
            EditorSceneManager.sceneOpened += OnEditorSceneOpened;
            // Auto-spawned atlases must be destroyed before scene close and
            // before play-mode entry, or Unity logs "Some objects were not
            // cleaned up when closing the scene."
            EditorSceneManager.sceneClosing += OnEditorSceneClosing;
            EditorApplication.playModeStateChanged += OnPlayModeStateChangedInstance;
        }

        private void OnEditorUpdate()
        {
            if (this == null) { EditorApplication.update -= OnEditorUpdate; return; }
            if (Application.isPlaying) return;
            EnsurePoolMatchesGrid();
            DrainPendingDeactivates();
            TryRender(EditorApplication.timeSinceStartup);
        }

        private void QueueEditorRender()
        {
            if (_editorRenderQueued) return;
            _editorRenderQueued = true;
            EditorApplication.QueuePlayerLoopUpdate();
            EditorApplication.update += EditorRenderOnce;
        }

        private void EditorRenderOnce()
        {
            EditorApplication.update -= EditorRenderOnce;
            _editorRenderQueued = false;
            if (this == null || Application.isPlaying) return;
            if (atlasCamera == null || atlasTexture == null) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            TryRender(EditorApplication.timeSinceStartup);
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        private void FlushPendingDeactivates()
        {
            EditorApplication.update -= FlushPendingDeactivates;
            _deactivateQueued = false;
            DrainPendingDeactivates();
        }

        private void OnEditorSceneOpened(UnityEngine.SceneManagement.Scene scene, OpenSceneMode mode)
            => EditorApplication.delayCall += EditorRegisterAllImages;

        // Auto-spawned atlases live in whichever scene was active when created.
        // If the user closes/changes scene or enters play mode, Unity warns
        // about "objects not cleaned up." We pre-empt that.
        private void OnEditorSceneClosing(UnityEngine.SceneManagement.Scene scene, bool removingScene)
        {
            if (!_autoSpawned || gameObject.scene != scene) return;
            DestroySafe(gameObject);
        }

        private void OnPlayModeStateChangedInstance(PlayModeStateChange state)
        {
            if (_autoSpawned && state == PlayModeStateChange.ExitingEditMode)
                DestroySafe(gameObject);
        }

        private void EditorRegisterAllImages()
        {
            if (this == null || Application.isPlaying) return;
            var images = Object.FindObjectsByType<MeshImage>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var img in images)
                if (img != null && img.isActiveAndEnabled) img.EditorReregister();
        }
#endif
    }
}