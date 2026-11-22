using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using static MeshImages.MeshImageAtlasUtility;
#if MESHIMAGES_URP
using UnityEngine.Rendering.Universal;
#endif
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
        // ---------------- Constants ----------------
        private const float MinFitDimension = 1e-5f;
        private const int MinCellResolution = 16;
        // Above this many changed cells in one frame, one full render is cheaper
        // than per-cell renders (each render request has a fixed CPU cost).
        private const int MaxCellRendersPerFrame = 8;
        private const GraphicsFormat AtlasColorFormat = GraphicsFormat.R8G8B8A8_SRGB;
        private const GraphicsFormat AtlasDepthFormat = GraphicsFormat.D24_UNorm_S8_UInt;

        // ---------------- Inspector ----------------
        [Header("References")]
        [SerializeField] private Camera atlasCamera;
        [SerializeField] private Material defaultMaterial;

        [Header("Layout")]
        [Tooltip("Cells per row/column. Total slots = gridSize * gridSize.")]
        [SerializeField, Min(1)] private int gridSize = 7;
        [Tooltip("Pixels per cell. Atlas texture = gridSize * cellResolution square.")]
        [SerializeField, Min(MinCellResolution)] private int cellResolution = 220;
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
        [Tooltip("Idle re-render rate (fps) for animated materials or lighting. Changes always " +
                 "render immediately. 0 = render only on change.")]
        [SerializeField] private float renderFps;

        // ---------------- State ----------------
        private readonly Dictionary<MeshImage, AtlasEntry> _entries = new();
        private readonly Dictionary<SlotKey, SharedSlot> _shared = new();
        // Images already told "atlas is full"; waiting images are retried on
        // every freed slot, so warn once per image until it gets placed.
        private readonly HashSet<MeshImage> _warnedFull = new();
        private readonly HashSet<int> _dirtyCells = new();
        private readonly MeshImageSlotPool _pool = new();
        private int _resolvedPreviewLayer, _lastCullingMask = -1;
        private double _lastRenderTime;
        private bool _dirty = true, _dirtyAll = true, _autoSpawned;
        // False for a duplicate atlas left idle in edit mode: it owns no
        // texture or pool until the registry promotes it.
        private bool _active;
        // Set when waiting MeshImages may now fit (atlas activated, slot freed);
        // drained from the update loop, outside OnValidate/OnEnable callbacks.
        private bool _retryWaiting;
        private RenderTexture _atlasTexture;
        // Scratch target for per-cell renders, copied into the atlas afterwards.
        private RenderTexture _cellTexture;

        private static bool _warnedNoRenderRequest, _warnedMaskNarrowed;

        // ---------------- Properties ----------------
        /// <summary>The active atlas; auto-spawns one from Resources if none exists.</summary>
        public static MeshImageAtlas Instance => AtlasRegistry.GetOrSpawn();

        public RenderTexture Texture => _atlasTexture;
        public Camera Camera => atlasCamera;

        internal bool IsActive => _active;
        internal int ImageCount => _entries.Count;
        internal int UsedSlotCount => _shared.Count;

        private float ViewHalfExtent => atlasCamera != null ? atlasCamera.orthographicSize : 0f;
        private float CellFillSize => objectFillFraction * (2f * ViewHalfExtent / gridSize);
        private int MaxSlots => gridSize * gridSize;
        private SlotLayoutConfig LayoutConfig => new(gridSize, uvPaddingFraction, previewDepth, ViewHalfExtent);
        private int EffectiveCellResolution =>
            Mathf.Clamp(cellResolution, MinCellResolution, SystemInfo.maxTextureSize / gridSize);
        private static bool CanCopyCells => (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0;

        // ---------------- Public API ----------------
        /// <summary>
        /// Place <paramref name="image"/> in the atlas. Returns false if it could
        /// not be placed. MeshImage calls this itself; call it directly only for
        /// custom registration flows.
        /// </summary>
        public bool Add(MeshImage image, Mesh mesh, Material material,
                        Vector3 translation, Vector3 rotation, Vector3 scale)
            => TryAdd(image, mesh, material, translation, rotation, scale);

        /// <summary>
        /// Remove <paramref name="image"/> from the atlas, freeing its slot once no
        /// other image shares it. MeshImage calls this itself when disabled.
        /// </summary>
        public void Remove(MeshImage image)
        {
            if (image == null) return;
            _warnedFull.Remove(image);
            if (!_entries.TryGetValue(image, out var entry)) return;
            _entries.Remove(image);

            var key = entry.Key;
            if (!_shared.TryGetValue(key, out var shared)) return;
            if (--shared.RefCount > 0) { _shared[key] = shared; return; }

            // A freed cell is no longer displayed, so it needs no re-render.
            _shared.Remove(key);
            _pool.ReleaseSlot(entry.Slot);
            _retryWaiting = true;
        }

        // Called from the update loop: give waiting images a chance at the
        // slots/atlas that just became available.
        internal void ProcessPendingRetries()
        {
            if (!_retryWaiting || !_active) return;
            _retryWaiting = false;
            MeshImage.RetryWaiting();
        }

        // Hand every image carried by this atlas back to the waiting set, and
        // nudge the active atlas (if another one) to pick them up.
        private void ReleaseImages()
        {
            foreach (var img in _entries.Keys)
                if (img != null) img.OnAtlasLost(this);
            _entries.Clear();
            _shared.Clear();
            var active = AtlasRegistry.Active;
            if (active != null && active != this && active._active) active._retryWaiting = true;
        }

        // DontSaveInEditor keeps an edit-mode auto-spawn out of saved scenes,
        // and doubles as the auto-spawned marker that survives domain reloads.
        internal void MarkAutoSpawned()
        {
            name = "[MeshImageAtlas]";
            gameObject.hideFlags = Application.isPlaying ? HideFlags.None : HideFlags.DontSaveInEditor;
            _autoSpawned = true;
        }

        // ---------------- Lifecycle ----------------
        private void OnEnable()
        {
            // Defensive: clear stale quit flag if domain-reload was disabled
            // and we missed the editor callback.
            AtlasRegistry.IsQuitting = false;

            // Recover the auto-spawned marker after a domain reload; the editor
            // hooks use it to destroy the atlas before scene close / play mode.
            if ((gameObject.hideFlags & HideFlags.DontSaveInEditor) != 0) _autoSpawned = true;

            if (IsInPreviewScene()) { Activate(); return; }

            if (!AtlasRegistry.TryClaim(this))
            {
                if (Application.isPlaying) { DestroySafe(gameObject); return; }
                // Edit mode: never destroy scene content. Stay idle instead,
                // handing any images carried from an earlier active life over.
                ReleaseImages();
                WarnIdle();
                return;
            }
            Activate();
        }

        internal void Activate()
        {
            _active = true;
            if (Application.isPlaying && transform.parent == null) DontDestroyOnLoad(gameObject);

            if (atlasCamera == null) atlasCamera = GetComponent<Camera>();

            CreateTextures();
            atlasCamera.targetTexture = _atlasTexture;
            // Lock aspect to the square RT so grid math holds if external code skewed it.
            atlasCamera.aspect = 1f;
            ConfigureCamera();
            ApplyPreviewLayer();

            _pool.Build(transform, gridSize, _resolvedPreviewLayer);
            ValidateConfiguration();

            // Re-place registrations carried across disable/enable so
            // MeshImages aren't stranded on a released RT.
            ReplaceCarriedEntries();
            MarkAllDirty();
            _retryWaiting = true;

#if UNITY_EDITOR
            if (!Application.isPlaying) ToggleEditorHooks(true);
#endif
        }

        // Once per atlas per editor session, not on every domain reload.
        private void WarnIdle()
        {
#if UNITY_EDITOR
            string key = $"MeshImages.IdleAtlasWarned.{GetInstanceID()}";
            if (SessionState.GetBool(key, false)) return;
            SessionState.SetBool(key, true);
#endif
            var active = AtlasRegistry.Active;
            Debug.LogWarning($"{nameof(MeshImageAtlas)} on '{name}': another atlas ('{(active ? active.name : "?")}') " +
                             "is already active, so this one stays idle. Remove one of them.", this);
        }

        private void OnDisable()
        {
            AtlasRegistry.Release(this);
            if (!_active) return;
            _active = false;

            // Keep _entries across disable so re-enabling can re-place every image,
            // but hide them meanwhile — their texture is about to be released.
            foreach (var img in _entries.Keys)
                if (img != null) img.ClearPlacement();
            _shared.Clear();
            _pool.Destroy();
            ReleaseTextures();

#if UNITY_EDITOR
            ToggleEditorHooks(false);
            _editorRenderQueued = false;
#endif
        }

        private void OnDestroy()
        {
            AtlasRegistry.Release(this);
            // Let images find (or, in the editor, re-spawn) another atlas.
            ReleaseImages();
        }

        private void OnApplicationQuit() => AtlasRegistry.IsQuitting = true;

        private void OnValidate()
        {
            if (gridSize < 1) gridSize = 1;
            if (cellResolution < MinCellResolution) cellResolution = MinCellResolution;
            if (previewDepth <= 0f) previewDepth = 0.0001f;
            if (renderFps < 0f) renderFps = 0f;
            objectFillFraction = Mathf.Clamp(objectFillFraction, 0.05f, 1f);
            uvPaddingFraction = Mathf.Clamp(uvPaddingFraction, 0f, 0.25f);
            // Pool/texture rebuild is deferred to EnsureLayout().
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || !_active) return;
            EnsureLayout();
            ProcessPendingRetries();
            TryRender(Time.unscaledTimeAsDouble);
        }

        private bool IsInPreviewScene()
        {
#if UNITY_EDITOR
            return EditorSceneManager.IsPreviewScene(gameObject.scene);
#else
            return false;
#endif
        }

        // ---------------- Add pipeline ----------------
        private bool TryAdd(MeshImage image, Mesh mesh, Material material, Vector3 translation, Vector3 eulerRotation, Vector3 scale)
        {
            if (!_active || image == null || _entries.ContainsKey(image)) return false;
            if (material == null) material = defaultMaterial;
            if (mesh == null || material == null) return false;

            EnsureLayout();

            var entry = new AtlasEntry
            {
                Mesh = mesh,
                Material = material,
                Translation = translation,
                Rotation = eulerRotation,
                Scale = scale,
            };
            var key = entry.Key;
            var cfg = LayoutConfig;

            if (_shared.TryGetValue(key, out var shared))
            {
                shared.RefCount++;
                _shared[key] = shared;
                entry.Slot = shared.Slot;
                image.SetUvFromAtlas(MeshImageSlotPool.GetSlotUvRect(shared.Slot, in cfg), _atlasTexture);
                _entries[image] = entry;
                return true;
            }

            if (!_pool.TryAcquireSlot(out int slot))
            {
                _warnedFull.RemoveWhere(img => img == null);
                if (_warnedFull.Add(image))
                    Debug.LogWarning($"{nameof(MeshImageAtlas)} on '{name}': atlas is full " +
                                     $"({MaxSlots} slots); cannot place '{image.name}'. " +
                                     $"Increase gridSize or remove a MeshImage.", image);
                return false;
            }

            // Roll back slot acquisition on any failure before the final _entries assignment.
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

                var s = _pool[slot];
                s.Filter.sharedMesh = mesh;
                s.Renderer.sharedMaterial = material;

                var t = s.Go.transform;
                t.localScale = finalScale;
                t.localEulerAngles = eulerRotation;
                t.localPosition = MeshImageSlotPool.GetSlotLocalPosition(slot, translation, in cfg) + centerOffset;

                image.SetUvFromAtlas(MeshImageSlotPool.GetSlotUvRect(slot, in cfg), _atlasTexture);

                entry.Slot = slot;
                _shared[key] = new SharedSlot { Slot = slot, RefCount = 1 };
                _entries[image] = entry;
                _warnedFull.Remove(image);
                MarkDirty(slot);
                return true;
            }
            catch
            {
                _pool.ReleaseSlot(slot);
                _shared.Remove(key);
                _entries.Remove(image);
                throw;
            }
        }

        // ---------------- Layout coordination ----------------
        // Rebuild the pool (and textures, if their size changed) when gridSize
        // or cellResolution changed, then re-place existing entries.
        internal bool NeedsLayoutRebuild => _active && (_pool.NeedsRebuild(gridSize) || TextureSizeChanged);

        private bool TextureSizeChanged =>
            _atlasTexture != null && _atlasTexture.width != gridSize * EffectiveCellResolution;

        internal void EnsureLayout()
        {
            bool sizeChanged = TextureSizeChanged;
            if (!_pool.NeedsRebuild(gridSize) && !sizeChanged) return;

            if (sizeChanged)
            {
                ReleaseTextures();
                CreateTextures();
                atlasCamera.targetTexture = _atlasTexture;
            }
            _pool.Build(transform, gridSize, _resolvedPreviewLayer);
            ReplaceCarriedEntries();
            MarkAllDirty();
        }

        // Snapshot current registrations, clear _entries, re-add into the
        // (presumed freshly built) pool. Used after disable/enable and layout
        // changes. Logs an aggregate diagnostic if capacity dropped.
        private void ReplaceCarriedEntries()
        {
            _shared.Clear();
            if (_entries.Count == 0) return;
            var carry = new List<KeyValuePair<MeshImage, AtlasEntry>>(_entries);
            _entries.Clear();

            int dropped = 0;
            for (int i = 0; i < carry.Count; i++)
            {
                var img = carry[i].Key;
                if (img == null) continue;
                var e = carry[i].Value;
                if (!TryAdd(img, e.Mesh, e.Material, e.Translation, e.Rotation, e.Scale))
                {
                    img.ClearPlacement();
                    dropped++;
                }
            }

            if (dropped > 0)
                Debug.LogWarning($"{nameof(MeshImageAtlas)} on '{name}': dropped {dropped} of " +
                                 $"{carry.Count} image(s) while rebuilding pool — capacity is " +
                                 $"{MaxSlots} slots. See per-image warnings above for names.", this);
        }

        // ---------------- Textures ----------------
        private void CreateTextures()
        {
            int cell = EffectiveCellResolution;
            if (_atlasTexture == null) _atlasTexture = CreateTarget($"{name}_AutoAtlas", gridSize * cell);
            if (_cellTexture == null) _cellTexture = CreateTarget($"{name}_Cell", cell);
        }

        private static RenderTexture CreateTarget(string textureName, int size)
        {
            var rt = new RenderTexture(size, size, AtlasColorFormat, AtlasDepthFormat)
            {
                name = textureName,
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        private void ReleaseTextures()
        {
            if (atlasCamera != null && atlasCamera.targetTexture == _atlasTexture)
                atlasCamera.targetTexture = null;
            ReleaseTarget(ref _atlasTexture);
            ReleaseTarget(ref _cellTexture);
        }

        private static void ReleaseTarget(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            DestroySafe(rt);
            rt = null;
        }

        // ---------------- Render ----------------
        private void MarkDirty(int cell)
        {
            _dirtyCells.Add(cell);
            RequestRender();
        }

        private void MarkAllDirty()
        {
            _dirtyAll = true;
            RequestRender();
        }

        private void RequestRender()
        {
            _dirty = true;
#if UNITY_EDITOR
            // OnEditorUpdate runs only when the editor ticks. Hop one editor-update
            // tick to render ASAP — far faster than EditorApplication.delayCall.
            // Rendering is forbidden during OnValidate, so we can't inline.
            if (!Application.isPlaying) QueueEditorRender();
#endif
        }

        private void TryRender(double now)
        {
            if (atlasCamera == null || _atlasTexture == null) return;

            if (atlasCamera.targetTexture != _atlasTexture)
                atlasCamera.targetTexture = _atlasTexture;
            // Defensive: re-lock aspect so external code can't skew the grid.
            atlasCamera.aspect = 1f;
            RefreshPreviewLayerIfNeeded();

#if UNITY_EDITOR
            if (EditorApplication.isCompiling || EditorApplication.isUpdating
                || BuildPipeline.isBuildingPlayer) return;
#endif

            if (!_dirty)
            {
                // Idle refresh (animated materials/lighting) redraws everything;
                // an empty atlas has nothing to show, so skip it.
                if (renderFps <= 0f || _shared.Count == 0) return;
                if (now - _lastRenderTime < 1.0 / renderFps) return;
                _dirtyAll = true;
            }
            _lastRenderTime = now;
            _dirty = false;

            if (_dirtyAll || _dirtyCells.Count > MaxCellRendersPerFrame || !CanCopyCells) RenderFull();
            else RenderCells(_dirtyCells);
            _dirtyAll = false;
            _dirtyCells.Clear();
        }

        internal void RenderFull()
        {
            _pool.SetRenderingEnabled(true);
            try
            {
                atlasCamera.ResetProjectionMatrix();
                Submit(_atlasTexture);
            }
            finally
            {
                _pool.SetRenderingEnabled(false);
            }
        }

        // Render each cell alone (projection narrowed to that cell) into the
        // scratch texture, then copy it into place. Cost scales with the cells
        // that changed instead of the whole atlas.
        internal void RenderCells(IEnumerable<int> cells)
        {
            var cfg = LayoutConfig;
            float cellSize = cfg.CellSize, half = cfg.ViewHalfExtent;
            float near = atlasCamera.nearClipPlane, far = atlasCamera.farClipPlane;
            int px = _cellTexture.width;

            _pool.SetRenderingEnabled(true);
            try
            {
                foreach (int cell in cells)
                {
                    if (cell < 0 || cell >= MaxSlots) continue;
                    int col = cell % gridSize, row = cell / gridSize;
                    float left = -half + col * cellSize, top = half - row * cellSize;
                    atlasCamera.projectionMatrix = Matrix4x4.Ortho(left, left + cellSize, top - cellSize, top, near, far);
                    Submit(_cellTexture);
                    // Texture y runs bottom-up; row 0 is the top row of the atlas.
                    Graphics.CopyTexture(_cellTexture, 0, 0, 0, 0, px, px,
                                         _atlasTexture, 0, 0, col * px, (gridSize - 1 - row) * px);
                }
            }
            finally
            {
                atlasCamera.ResetProjectionMatrix();
                _pool.SetRenderingEnabled(false);
            }
        }

        // Slot renderers stay force-hidden so scene/game cameras whose culling
        // mask overlaps the preview layer never draw them; both render paths
        // below are synchronous, so unhiding just around them is enough.
        private void Submit(RenderTexture target)
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                var req = new RenderPipeline.StandardRequest { destination = target };
                if (RenderPipeline.SupportsRenderRequest(atlasCamera, req))
                {
                    RenderPipeline.SubmitRenderRequest(atlasCamera, req);
                    return;
                }
                if (!_warnedNoRenderRequest)
                {
                    _warnedNoRenderRequest = true;
                    Debug.LogWarning($"{nameof(MeshImageAtlas)}: the active render pipeline does not " +
                                     "support render requests; falling back to Camera.Render().", this);
                }
            }
            var previous = atlasCamera.targetTexture;
            atlasCamera.targetTexture = target;
            atlasCamera.Render();
            atlasCamera.targetTexture = previous;
        }

        // ---------------- Camera & validation ----------------
        // The atlas only ever draws its slot meshes, so skip every URP extra:
        // HDR/MSAA intermediates, shadow maps, depth/opaque texture copies, post.
        private void ConfigureCamera()
        {
            atlasCamera.enabled = false; // we drive rendering manually
            atlasCamera.allowHDR = false;
            atlasCamera.allowMSAA = false;
#if MESHIMAGES_URP
            if (atlasCamera.TryGetComponent<UniversalAdditionalCameraData>(out var urp))
            {
                urp.renderShadows = false;
                urp.requiresDepthOption = CameraOverrideOption.Off;
                urp.requiresColorOption = CameraOverrideOption.Off;
                urp.renderPostProcessing = false;
                urp.antialiasing = AntialiasingMode.None;
            }
#endif
        }

        private void ValidateConfiguration()
        {
            string prefix = $"{nameof(MeshImageAtlas)} on '{name}'";
            if (defaultMaterial == null)
                Debug.LogWarning($"{prefix}: no defaultMaterial assigned.", this);
            if (!atlasCamera.orthographic)
                Debug.LogWarning($"{prefix}: camera is not orthographic.", this);
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

        // The atlas renders exactly one layer, the one its slots live on. Any
        // extra layer only adds culling work and lets nearby scene objects
        // leak into the thumbnails, so narrow the mask (warning once).
        private void ApplyPreviewLayer()
        {
            _resolvedPreviewLayer = ResolvePreviewLayer();
            int single = 1 << _resolvedPreviewLayer;
            if (atlasCamera.cullingMask != single)
            {
                if (!_warnedMaskNarrowed)
                {
                    _warnedMaskNarrowed = true;
                    Debug.LogWarning($"{nameof(MeshImageAtlas)} on '{name}': the camera culling mask must be a " +
                                     $"single layer; using '{LayerMask.LayerToName(_resolvedPreviewLayer)}'. " +
                                     "Pick a layer no scene objects near the atlas use.", this);
                }
                atlasCamera.cullingMask = single;
            }
            _lastCullingMask = single;
        }

        private void RefreshPreviewLayerIfNeeded()
        {
            if (atlasCamera == null || _pool.Capacity == 0) return;
            if (atlasCamera.cullingMask == _lastCullingMask) return;

            int oldLayer = _resolvedPreviewLayer;
            ApplyPreviewLayer();
            if (_resolvedPreviewLayer != oldLayer) _pool.SetPreviewLayer(_resolvedPreviewLayer);
        }
    }
}
