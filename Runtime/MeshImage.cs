using UnityEngine;
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
        [SerializeField] private new Material material;
        [SerializeField] private Vector3 translation;
        [SerializeField] private Vector3 rotation;
        [SerializeField] private Vector3 scale;

        private bool _registered;
        private MeshImageAtlas _atlas;
#if UNITY_EDITOR
        private bool _reregisterPending;
#endif

        /// <summary>Apply many fields atomically, with one re-register at the end.</summary>
        public void Configure(Mesh mesh = null, Material material = null,
                              Vector3? translation = null, Vector3? rotation = null, Vector3? scale = null)
        {
            bool changed = false;
            if (mesh != null && this.mesh != mesh) { this.mesh = mesh; changed = true; }
            if (material != null && this.material != material) { this.material = material; changed = true; }
            if (rotation.HasValue && this.rotation != rotation.Value) { this.rotation = rotation.Value; changed = true; }
            if (scale.HasValue && this.scale != scale.Value) { this.scale = scale.Value; changed = true; }
            if (translation.HasValue && this.translation != translation.Value) { this.translation = translation.Value; changed = true; }
            if (changed) Reregister();
        }

        // ---------- Lifecycle ----------
        protected override void OnEnable()
        {
            base.OnEnable();
#if UNITY_EDITOR
            if (BuildPipeline.isBuildingPlayer) return;
            // Re-attempt registration if the atlas is recreated later.
            EditorApplication.update -= EditorRegistrationPoll;
            EditorApplication.update += EditorRegistrationPoll;
#endif
            Register();
        }

        protected override void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.update -= EditorRegistrationPoll;
#endif
            Unregister();
            base.OnDisable();
        }

        private void Register()
        {
            if (_registered) return;

            // Re-resolve every time; cached refs can be stale across domain
            // reloads. Use ExistingInstance first — we may be inside OnValidate
            // where Instantiate would parent a new GameObject and trip
            // "OnTransformChildrenChanged during OnValidate". If we're at
            // runtime (not OnValidate), it's safe to fall back to Instance,
            // which auto-spawns the atlas from Resources if needed. Without
            // this fallback, a MeshImage that wakes up before any atlas
            // exists in the scene would silently never register — common in
            // builds where loading order isn't guaranteed.
            if (_atlas == null) _atlas = MeshImageAtlas.ExistingInstance;
            if (_atlas == null && Application.isPlaying) _atlas = MeshImageAtlas.Instance;
            if (_atlas == null) return;

            if (texture == null && _atlas.Texture != null) texture = _atlas.Texture;
            _atlas.Add(this, mesh, material, translation, rotation, scale);
            _registered = true;
        }

        private void Unregister()
        {
            if (!_registered) return;
            // Never auto-spawn from here — see Register comment.
            var atlas = (_atlas != null) ? _atlas : MeshImageAtlas.ExistingInstance;
            if (atlas != null) atlas.Remove(this);
            _registered = false;
            _atlas = null;
        }

        private void Reregister()
        {
            if (!isActiveAndEnabled) return;
            Unregister();
            Register();
        }

        internal void SetUvFromAtlas(Rect uv) => uvRect = uv;

        // ---------- Editor ----------
#if UNITY_EDITOR
        internal void EditorReregister() { Unregister(); Register(); }

        // Idle check: if we're unregistered (e.g. atlas was deleted) but the
        // atlas is reachable again, register. Touching Instance auto-spawns
        // it when it's safe to do so.
        private void EditorRegistrationPoll()
        {
            if (this == null) { EditorApplication.update -= EditorRegistrationPoll; return; }
            if (Application.isPlaying || _registered || !isActiveAndEnabled) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (MeshImageAtlas.Instance == null) return;
            Register();
        }

        protected override void Reset()
        {
            base.Reset();
            if (Application.isPlaying)
            {
                if (isActiveAndEnabled) ScheduleRuntimeReregister();
            }
            // Reset() runs during component construction — DestroyImmediate /
            // texture wiring shouldn't happen mid-construction, so defer.
            else ScheduleEditorFullSetup();
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            if (Application.isPlaying)
            {
                // OnValidate runs in a restricted context where SetActive /
                // SendMessage are forbidden — defer in play mode.
                if (isActiveAndEnabled) ScheduleRuntimeReregister();
            }
            else if (isActiveAndEnabled)
            {
                // Edit mode tolerates synchronous reregister here; the atlas
                // defers its own forbidden ops (Camera.Render, SetActive)
                // through its own update hops, so this stays warning-free.
                EditorReregister();
            }
            else ScheduleEditorFullSetup();
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

        private void ScheduleEditorFullSetup()
        {
            var self = this;
            EditorApplication.delayCall += () =>
            {
                if (self == null || Application.isPlaying) return;
                self.StripSiblingRawImages();
                self.WireAtlasTexture();
                self.EditorReregister();
            };
        }

        private void StripSiblingRawImages()
        {
            var graphics = GetComponents<RawImage>();
            foreach (var g in graphics)
                if (g != null && g != this && g.GetType() == typeof(RawImage))
                    DestroyImmediate(g);
        }

        private void WireAtlasTexture()
        {
            var atlas = MeshImageAtlas.ExistingInstance;
            var rt = atlas != null ? atlas.Texture : null;
            if (rt != null && texture != rt) texture = rt;
        }
#endif
    }
}