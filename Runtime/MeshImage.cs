using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace MeshImages
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class MeshImage : RawImage
    {
        [Header("Preview")]
        [SerializeField] private Mesh mesh;
        [FormerlySerializedAs("material")]
        [SerializeField] private Material previewMaterial;

        [SerializeField] private Vector3 translation;
        [SerializeField] private Vector3 rotation;
        [SerializeField] private Vector3 scale;

        private bool _registered;
        private MeshImageAtlas _atlas;

        /// <summary>Apply many fields atomically, with one re-register at the end.</summary>
        public void Configure(Mesh mesh = null, Material material = null,
                              Vector3? translation = null, Vector3? rotation = null, Vector3? scale = null)
        {
            bool changed = false;

            if (mesh != null && this.mesh != mesh)
            { this.mesh = mesh; changed = true; }

            if (material != null && this.previewMaterial != material)
            { this.previewMaterial = material; changed = true; }

            if (rotation.HasValue && this.rotation != rotation.Value)
            { this.rotation = rotation.Value; changed = true; }

            if (scale.HasValue && this.scale != scale.Value)
            { this.scale = scale.Value; changed = true; }

            if (translation.HasValue && this.translation != translation.Value)
            { this.translation = translation.Value; changed = true; }

            if (changed)
                Reregister();
        }

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
            base.OnDisable();
        }

        private void Register()
        {
            if (_registered) return;

            if (_atlas == null)
                _atlas = MeshImageAtlas.Instance ?? FindAnyObjectByType<MeshImageAtlas>();

            if (_atlas == null) return;

            if (texture == null && _atlas.Texture != null)
                texture = _atlas.Texture;

            _atlas.Add(this, mesh, previewMaterial, translation, rotation, scale);

            _registered = true;
        }

        private void Unregister()
        {
            if (!_registered) return;

            var atlas = MeshImageAtlas.Instance;
            if (atlas != null)
                atlas.Remove(this);

            _registered = false;
        }

        private void Reregister()
        {
            if (!_registered) return;
            if (!isActiveAndEnabled) return;

            Unregister();
            Register();
        }

        // ---------- UV from atlas ----------

        internal void SetUvFromAtlas(Rect uv) => uvRect = uv;

        // ---------- Editor ----------

#if UNITY_EDITOR
        internal void EditorReregister()
        {
            Unregister();
            Register();
        }

        protected override void Reset()
        {
            base.Reset();
            ScheduleEditorRefresh();
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            ScheduleEditorRefresh();
        }

        private void ScheduleEditorRefresh()
        {
            var self = this;
            EditorApplication.delayCall += () =>
            {
                if (self == null) return;
                if (Application.isPlaying) return;
                self.StripSiblingRawImages();
                self.WireAtlasTexture();
                self.EditorReregister();
            };
        }

        private void StripSiblingRawImages()
        {
            var graphics = GetComponents<RawImage>();
            for (int i = 0; i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null) continue;
                if (g == this) continue;
                if (g.GetType() != typeof(RawImage)) continue;
                DestroyImmediate(g);
            }
        }

        private void WireAtlasTexture()
        {
            var atlas = FindAtlas();
            if (atlas == null) return;

            var rt = atlas.Texture;
            if (rt == null) return;
            if (texture == rt) return;

            texture = rt;
        }

        private static MeshImageAtlas FindAtlas()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
            {
                foreach (var root in stage.scene.GetRootGameObjects())
                {
                    var found = root.GetComponentInChildren<MeshImageAtlas>(true);
                    if (found != null) return found;
                }
            }

            return Object.FindAnyObjectByType<MeshImageAtlas>(FindObjectsInactive.Include);
        }
#endif
    }
}
