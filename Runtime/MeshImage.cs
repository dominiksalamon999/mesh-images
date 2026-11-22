using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MeshImages
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class MeshImage : RawImage
    {
        [SerializeField] private Mesh mesh;
        [Tooltip("Material used to render the mesh. Empty = the atlas's default material.")]
        [SerializeField, FormerlySerializedAs("material")] private Material meshMaterial;
        [SerializeField] private Vector3 translation;
        [SerializeField] private Vector3 rotation;
        [SerializeField] private Vector3 scale = Vector3.one;

        private bool _registered;
        // True only while the atlas holds a slot for us; otherwise draw nothing
        // rather than the whole atlas or a slot now owned by another image.
        private bool _placed;
        // Atlas placement is runtime state: kept off RawImage's serialized m_UVRect / m_Texture, so a scene or
        // prefab save never records which slot this image happened to get (slots go in registration order).
        [System.NonSerialized] private Rect _slotUv = new Rect(0f, 0f, 1f, 1f);
        [System.NonSerialized] private Texture _atlasTexture;
        private MeshImageAtlas _atlas;
#if UNITY_EDITOR
        // NonSerialized: a script reload keeps private fields but drops the
        // pending Invoke/delayCall, which would leave this stuck at true and
        // silently swallow every later inspector edit.
        [System.NonSerialized] private bool _reregisterPending;
#endif

        // ---------- Public API ----------
        /// <summary>Mesh to render. Null clears the image.</summary>
        public Mesh Mesh
        {
            get => mesh;
            set { if (mesh == value) return; mesh = value; Reregister(); }
        }

        /// <summary>Material for the mesh. Null uses the atlas's default material.</summary>
        public Material MeshMaterial
        {
            get => meshMaterial;
            set { if (meshMaterial == value) return; meshMaterial = value; Reregister(); }
        }

        public Vector3 Translation
        {
            get => translation;
            set { if (translation == value) return; translation = value; Reregister(); }
        }

        /// <summary>Euler angles, in degrees.</summary>
        public Vector3 Rotation
        {
            get => rotation;
            set { if (rotation == value) return; rotation = value; Reregister(); }
        }

        public Vector3 Scale
        {
            get => scale;
            set { if (scale == value) return; scale = value; Reregister(); }
        }

        /// <summary>
        /// Apply many fields atomically, with one re-register at the end.
        /// Null arguments leave a field unchanged; use the properties to clear one.
        /// </summary>
        public void Configure(Mesh mesh = null, Material material = null,
                              Vector3? translation = null, Vector3? rotation = null, Vector3? scale = null)
        {
            bool changed = false;
            if (mesh != null && this.mesh != mesh) { this.mesh = mesh; changed = true; }
            if (material != null && meshMaterial != material) { meshMaterial = material; changed = true; }
            if (rotation.HasValue && this.rotation != rotation.Value) { this.rotation = rotation.Value; changed = true; }
            if (scale.HasValue && this.scale != scale.Value) { this.scale = scale.Value; changed = true; }
            if (translation.HasValue && this.translation != translation.Value) { this.translation = translation.Value; changed = true; }
            if (changed) Reregister();
        }

        internal bool IsPlaced => _placed;
        internal Rect SlotUv => _slotUv;

        public override Texture mainTexture => _placed && _atlasTexture != null ? _atlasTexture : base.mainTexture;

        // ---------- Lifecycle ----------
        protected override void OnEnable()
        {
            base.OnEnable();
#if UNITY_EDITOR
            if (BuildPipeline.isBuildingPlayer) return;
#endif
            Register();
        }

        protected override void OnDisable()
        {
            Unregister();
            SetWaitingForAtlas(false);
#if UNITY_EDITOR
            // A pending Invoke never fires on an inactive object; don't let the
            // flag block every later play-mode inspector edit.
            _reregisterPending = false;
            CancelInvoke(nameof(RuntimeReregisterNow));
#endif
            base.OnDisable();
        }

        private void Register()
        {
            // Disabled components and prefab assets (which get OnValidate but
            // are never active) must not hold an atlas slot. Without a mesh
            // there is nothing to place; the Mesh setter re-registers.
            if (_registered || !isActiveAndEnabled || mesh == null) return;

            // Look up without spawning first (spawning is unsafe in some editor
            // callbacks). In play mode, spawn if needed: load order isn't
            // guaranteed, so the atlas may not exist yet.
            if (_atlas == null) _atlas = AtlasRegistry.ExistingFor(this);
            if (_atlas == null && Application.isPlaying) _atlas = AtlasRegistry.GetOrSpawn();
            // Not placed (no atlas yet, atlas full): wait. An atlas retries
            // waiting images when it activates or frees a slot.
            if (_atlas == null || !_atlas.Add(this, mesh, meshMaterial, translation, rotation, scale))
            {
                _atlas = null;
                SetWaitingForAtlas(true);
                return;
            }
            _registered = true;
            SetWaitingForAtlas(false);
        }

        private void Unregister()
        {
            if (!_registered) return;
            // Never auto-spawn from here — see Register comment.
            var atlas = (_atlas != null) ? _atlas : AtlasRegistry.ExistingFor(this);
            if (atlas != null) atlas.Remove(this);
            ClearPlacement();
            _registered = false;
            _atlas = null;
        }

        internal void Reregister()
        {
            if (!isActiveAndEnabled) return;
            Unregister();
            Register();
        }

        internal void SetUvFromAtlas(Rect uv, Texture atlasTexture)
        {
            bool moved = _slotUv != uv;
            _slotUv = uv;
            if (_atlasTexture != atlasTexture)
            {
                _atlasTexture = atlasTexture;
                SetMaterialDirty();   // Graphic re-reads mainTexture
            }
            if (_placed && !moved) return;
            _placed = true;
            SetVerticesDirty();
        }

        internal void ClearPlacement()
        {
            if (!_placed) return;
            _placed = false;
            _atlasTexture = null;
            SetMaterialDirty();
            SetVerticesDirty();
        }

        // Our atlas is being destroyed or went idle. It already dropped us, so
        // forget it and wait for another atlas (or, in the editor, a fresh auto-spawn).
        internal void OnAtlasLost(MeshImageAtlas atlas)
        {
            if (_atlas != atlas) return;
            _atlas = null;
            _registered = false;
            ClearPlacement();
            if (isActiveAndEnabled) SetWaitingForAtlas(true);
        }

        // RawImage's quad, but with the runtime slot rect instead of the serialized m_UVRect.
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Texture tex = mainTexture;
            if (!_placed || tex == null) return;

            Rect r = GetPixelAdjustedRect();
            var v = new Vector4(r.x, r.y, r.x + r.width, r.y + r.height);
            float sx = tex.width * tex.texelSize.x, sy = tex.height * tex.texelSize.y;
            Color32 c = color;
            vh.AddVert(new Vector3(v.x, v.y), c, new Vector2(_slotUv.xMin * sx, _slotUv.yMin * sy));
            vh.AddVert(new Vector3(v.x, v.w), c, new Vector2(_slotUv.xMin * sx, _slotUv.yMax * sy));
            vh.AddVert(new Vector3(v.z, v.w), c, new Vector2(_slotUv.xMax * sx, _slotUv.yMax * sy));
            vh.AddVert(new Vector3(v.z, v.y), c, new Vector2(_slotUv.xMax * sx, _slotUv.yMin * sy));
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }

        // ---------- Waiting for an atlas ----------
        // Enabled images with a mesh but no slot (no atlas yet, or atlas full).
        // An atlas retries them when it activates or frees a slot; in the
        // editor a throttled poll also auto-spawns an atlas when none exists.
        private static readonly HashSet<MeshImage> s_waitingForAtlas = new();

        private void SetWaitingForAtlas(bool waiting)
        {
            if (waiting) s_waitingForAtlas.Add(this);
            else s_waitingForAtlas.Remove(this);
        }

        internal static void RetryWaiting()
        {
            s_waitingForAtlas.RemoveWhere(img => img == null || !img.isActiveAndEnabled);
            if (s_waitingForAtlas.Count == 0) return;
            foreach (var img in new List<MeshImage>(s_waitingForAtlas))
                img.Register();
        }

        // ---------- Editor ----------
#if UNITY_EDITOR
        private const double WaitingPollInterval = 0.25;
        private static double s_nextWaitingPoll;

        [InitializeOnLoadMethod]
        private static void InitWaitingPoll()
        {
            EditorApplication.update -= PollWaitingForAtlas;
            EditorApplication.update += PollWaitingForAtlas;
        }

        // Only spawns (or promotes) an atlas when none is active; that atlas's
        // activation then retries the waiting images. Images waiting on a full
        // atlas are left alone, so this never re-logs "atlas is full".
        private static void PollWaitingForAtlas()
        {
            if (s_waitingForAtlas.Count == 0 || Application.isPlaying) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < s_nextWaitingPoll) return;
            s_nextWaitingPoll = now + WaitingPollInterval;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            s_waitingForAtlas.RemoveWhere(img => img == null || !img.isActiveAndEnabled);
            if (s_waitingForAtlas.Count == 0 || AtlasRegistry.ExistingFor(null) != null) return;
            AtlasRegistry.GetOrSpawn();
        }

        protected override void Reset()
        {
            base.Reset();
            if (!isActiveAndEnabled) return;
            if (Application.isPlaying) ScheduleRuntimeReregister();
            // Reset() runs during component construction; defer the re-place.
            else ScheduleEditorReregister();
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            // Disabled components and prefab assets hold no slot; nothing to do.
            if (!isActiveAndEnabled) return;
            if (Application.isPlaying) { ScheduleRuntimeReregister(); return; }
            // Edit mode: re-place right away so inspector drags feel live.
            // Only creating/destroying objects is unsafe inside OnValidate, and
            // that happens only when the atlas must rebuild its layout; defer
            // then. (delayCall only runs after inspector updates, so deferring
            // every edit adds visible lag.)
            var atlas = AtlasRegistry.ExistingFor(this);
            if (atlas != null && atlas.NeedsLayoutRebuild) ScheduleEditorReregister();
            else Reregister();
        }

        private void ScheduleRuntimeReregister()
        {
            if (_reregisterPending) return;
            _reregisterPending = true;
            // Invoke(0) runs after OnValidate returns but BEFORE next render,
            // so changes show up same frame (yield return null is one frame out).
            Invoke(nameof(RuntimeReregisterNow), 0f);
        }

        private void RuntimeReregisterNow()
        {
            _reregisterPending = false;
            if (this == null || !isActiveAndEnabled) return;
            Reregister();
        }

        private void ScheduleEditorReregister()
        {
            if (_reregisterPending) return;
            _reregisterPending = true;
            var self = this;
            EditorApplication.delayCall += () =>
            {
                if (self == null) return;
                self._reregisterPending = false;
                if (Application.isPlaying) return;
                self.Reregister();
            };
        }
#endif
    }
}
