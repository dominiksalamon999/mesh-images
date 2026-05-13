using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace MeshImages
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public class MeshImageAtlas : MonoBehaviour
    {
        private const float MinFitDimension = 1e-5f;
        private const string RuntimeObjectName = "MeshImage_Runtime";

        private const int AtlasSize = 1536;
        private const GraphicsFormat AtlasColorFormat = GraphicsFormat.R8G8B8A8_SRGB;
        private const GraphicsFormat AtlasDepthFormat = GraphicsFormat.D24_UNorm_S8_UInt;
        private const int AtlasAntiAliasing = 1;
        private const FilterMode AtlasFilterMode = FilterMode.Bilinear;
        private const TextureWrapMode AtlasWrapMode = TextureWrapMode.Clamp;
        private const int AtlasAnisoLevel = 0;
        private const bool AtlasUseMipMap = false;
        private const bool AtlasAutoGenerateMips = false;

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
        [SerializeField] private float renderFps = 5f;

        private float ViewHalfExtent => atlasCamera != null ? atlasCamera.orthographicSize : 0f;
        private float ViewSize => 2f * ViewHalfExtent;
        private float CellSize => ViewSize / gridSize;
        private float CellFillSize => objectFillFraction * CellSize;
        private int MaxSlots => gridSize * gridSize;

        private struct AtlasEntry
        {
            public GameObject Object;
            public int Slot;
        }

        private readonly Dictionary<MeshImage, AtlasEntry> _pairs = new();
        private readonly Queue<int> _freeSlots = new();
        private int _nextSlot;
        private int _resolvedPreviewLayer;
        private double _lastRenderTime;

        // Runtime-only, not serialized. Always created and owned by this component.
        private RenderTexture atlasTexture;

        public static MeshImageAtlas Instance { get; private set; }
        public RenderTexture Texture => atlasTexture;
        public Camera Camera => atlasCamera;

        // ---------- Lifecycle ----------

        private void OnEnable()
        {
            Instance = this;

            if (atlasCamera == null)
                atlasCamera = GetComponent<Camera>();

            CreateAtlasTexture();

            atlasCamera.targetTexture = atlasTexture;
            atlasCamera.enabled = false; // we drive Render() manually

            _resolvedPreviewLayer = ResolvePreviewLayer();
            ValidateConfiguration();

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                EditorApplication.update -= OnEditorUpdate;
                EditorApplication.update += OnEditorUpdate;
                EditorApplication.delayCall += EditorRegisterAllImages;
                EditorSceneManager.sceneOpened -= OnEditorSceneOpened;
                EditorSceneManager.sceneOpened += OnEditorSceneOpened;
            }
#endif
        }

        private void OnDisable()
        {
            foreach (var entry in _pairs.Values)
                if (entry.Object != null) DestroySafe(entry.Object);

            _pairs.Clear();
            _freeSlots.Clear();
            _nextSlot = 0;

            ReleaseAtlasTexture();

            if (Instance == this) Instance = null;

#if UNITY_EDITOR
            EditorApplication.update -= OnEditorUpdate;
            EditorSceneManager.sceneOpened -= OnEditorSceneOpened;
#endif
        }

        private void OnValidate()
        {
            if (gridSize < 1) gridSize = 1;
            if (previewDepth <= 0f) previewDepth = 0.0001f;
            if (renderFps < 0f) renderFps = 0f;
        }

        private void CreateAtlasTexture()
        {
            if (atlasTexture != null) return;

            atlasTexture = new RenderTexture(AtlasSize, AtlasSize, AtlasColorFormat, AtlasDepthFormat)
            {
                name = $"{name}_AutoAtlas",
                antiAliasing = AtlasAntiAliasing,
                filterMode = AtlasFilterMode,
                wrapMode = AtlasWrapMode,
                anisoLevel = AtlasAnisoLevel,
                useMipMap = AtlasUseMipMap,
                autoGenerateMips = AtlasAutoGenerateMips,
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

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;
            TryRender(Time.unscaledTimeAsDouble);
        }

#if UNITY_EDITOR
        private void OnEditorUpdate()
        {
            if (this == null) { EditorApplication.update -= OnEditorUpdate; return; }
            if (Application.isPlaying) return;
            TryRender(EditorApplication.timeSinceStartup);
        }
#endif

        private void TryRender(double now)
        {
            if (atlasCamera == null) return;
            if (atlasTexture == null) return;

            if (atlasCamera.targetTexture != atlasTexture)
                atlasCamera.targetTexture = atlasTexture;

#if UNITY_EDITOR
            if (EditorApplication.isCompiling) return;
            if (EditorApplication.isUpdating) return;
            if (BuildPipeline.isBuildingPlayer) return;
#endif

            if (renderFps > 0f)
            {
                double interval = 1.0 / renderFps;
                if (now - _lastRenderTime < interval) return;
            }
            _lastRenderTime = now;

            atlasCamera.Render();
        }

        private void ValidateConfiguration()
        {
            if (defaultMaterial == null)
                Debug.LogWarning($"{nameof(MeshImageAtlas)} on '{name}': no defaultMaterial assigned.", this);

            if (!atlasCamera.orthographic)
                Debug.LogWarning($"{nameof(MeshImageAtlas)} on '{name}': camera is not orthographic.", this);

            if (atlasCamera.cullingMask == 0)
                Debug.LogWarning($"{nameof(MeshImageAtlas)} on '{name}': camera culling mask is empty.", this);
        }

        // Pick the lowest layer present in the camera's culling mask so previews
        // are guaranteed to be rendered. If the mask is empty, fall back to the
        // camera GameObject's own layer.
        private int ResolvePreviewLayer()
        {
            int mask = atlasCamera.cullingMask;
            if (mask != 0)
            {
                for (int i = 0; i < 32; i++)
                    if ((mask & (1 << i)) != 0) return i;
            }
            return atlasCamera.gameObject.layer;
        }

        // ---------- Public API ----------

        public void Add(MeshImage image, Mesh mesh, Material material,
                        Vector3 position, Vector3 rotation, Vector3 scale)
        {
            TryAdd(image, mesh, material, position, rotation, scale);
        }

        internal void Remove(MeshImage image)
        {
            if (image == null) return;
            if (!_pairs.TryGetValue(image, out var entry)) return;

            if (entry.Object != null) DestroySafe(entry.Object);

            _freeSlots.Enqueue(entry.Slot);
            _pairs.Remove(image);
        }

        // ---------- Core ----------

        private bool TryAdd(MeshImage image, Mesh mesh, Material material,
                            Vector3 position, Vector3 eulerRotation, Vector3 scale)
        {
            if (image == null) return false;
            if (_pairs.ContainsKey(image)) return false;

            if (material == null) material = defaultMaterial;
            if (mesh == null || material == null) return false;

            if (!TryAcquireSlot(out int slot)) return false;

            var uv = GetSlotUvRect(slot);
            var go = CreateRuntimeObject(mesh, material);

            var rotation = Quaternion.Euler(eulerRotation);

            float fitScale;
            if (autoFit)
            {
                var unscaledRotated = ComputeRotatedScaledAabb(mesh, Vector3.one, rotation);
                float maxXY = Mathf.Max(unscaledRotated.size.x, unscaledRotated.size.y);
                fitScale = (maxXY > MinFitDimension) ? (CellFillSize / maxXY) : CellFillSize;
            }
            else
            {
                fitScale = CellFillSize;
            }

            Vector3 finalScale = scale * fitScale;

            Vector3 centerOffset = Vector3.zero;
            if (autoCenter)
            {
                var finalRotated = ComputeRotatedScaledAabb(mesh, finalScale, rotation);
                centerOffset = new Vector3(-finalRotated.center.x, -finalRotated.center.y, 0f);
            }

            var t = go.transform;
            t.localScale = finalScale;
            t.localEulerAngles = eulerRotation;
            t.localPosition = GetSlotLocalPosition(slot, position) + centerOffset;

            image.SetUvFromAtlas(uv);
            image.texture = atlasTexture;

            _pairs[image] = new AtlasEntry { Object = go, Slot = slot };
            return true;
        }

        private GameObject CreateRuntimeObject(Mesh mesh, Material material)
        {
            var go = new GameObject(RuntimeObjectName, typeof(MeshFilter), typeof(MeshRenderer));
            go.hideFlags = HideFlags.HideAndDontSave;
            go.layer = _resolvedPreviewLayer;
            go.transform.SetParent(this.transform, false);

            go.GetComponent<MeshFilter>().sharedMesh = mesh;

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            return go;
        }

        private bool TryAcquireSlot(out int slot)
        {
            if (_freeSlots.Count > 0) { slot = _freeSlots.Dequeue(); return true; }
            if (_nextSlot >= MaxSlots)
            {
                Debug.LogWarning($"{nameof(MeshImageAtlas)} on '{name}': atlas is full " +
                                 $"({MaxSlots} slots). Increase gridSize or remove a MeshImage.", this);
                slot = -1;
                return false;
            }
            slot = _nextSlot++;
            return true;
        }

        private Rect GetSlotUvRect(int slot)
        {
            float v = 1f / gridSize;
            float pad = v * uvPaddingFraction;

            int col = slot % gridSize;
            int row = slot / gridSize;

            return new Rect(
                col * v + pad,
                (1f - v) - row * v + pad,
                v - 2f * pad,
                v - 2f * pad);
        }

        // Slot center in camera-local space. Top-left of the view = (-ViewHalfExtent, +ViewHalfExtent).
        private Vector3 GetSlotLocalPosition(int slot, Vector3 offset)
        {
            int col = slot % gridSize;
            int row = slot / gridSize;
            float cell = CellSize;

            float x = -ViewHalfExtent + (col + 0.5f) * cell;
            float y = +ViewHalfExtent - (row + 0.5f) * cell;

            return new Vector3(x, y, previewDepth) + offset;
        }

        // AABB of mesh.bounds after applying scale and rotation. Translation is ignored.
        private static Bounds ComputeRotatedScaledAabb(Mesh mesh, Vector3 scale, Quaternion rotation)
        {
            var b = mesh.bounds;
            var c = b.center;
            var e = b.extents;

            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;

            for (int i = 0; i < 8; i++)
            {
                float xs = ((i & 1) == 0) ? +e.x : -e.x;
                float ys = ((i & 2) == 0) ? +e.y : -e.y;
                float zs = ((i & 4) == 0) ? +e.z : -e.z;

                var corner = new Vector3(c.x + xs, c.y + ys, c.z + zs);
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

#if UNITY_EDITOR
        private void OnEditorSceneOpened(UnityEngine.SceneManagement.Scene scene, OpenSceneMode mode)
        {
            EditorApplication.delayCall += EditorRegisterAllImages;
        }

        private void EditorRegisterAllImages()
        {
            if (this == null || Application.isPlaying) return;

            var images = Object.FindObjectsByType<MeshImage>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var img in images)
                if (img != null && img.isActiveAndEnabled)
                    img.EditorReregister();
        }
#endif
    }
}
