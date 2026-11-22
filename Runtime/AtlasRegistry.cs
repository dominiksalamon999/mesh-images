using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace MeshImages
{
    /// <summary>
    /// Decides which <see cref="MeshImageAtlas"/> is active and finds (or
    /// auto-spawns) one for images. An atlas is in one of four states:
    /// <list type="bullet">
    /// <item><b>Active</b>: the one scene atlas all images use (claimed via <see cref="TryClaim"/>).
    /// An atlas placed in a scene replaces an auto-spawned one.</item>
    /// <item><b>Idle</b>: an edit-mode duplicate of a placed atlas; takes over if the active one goes away.</item>
    /// <item><b>Prefab mode</b>: active, but serves only images in the prefab being edited.</item>
    /// <item><b>Disabled</b>: switched off by the user; blocks auto-spawning a replacement.</item>
    /// </list>
    /// </summary>
    internal static class AtlasRegistry
    {
        private static MeshImageAtlas s_active;

        /// <summary>Set on application quit / play-mode exit so nothing spawns during teardown.</summary>
        internal static bool IsQuitting;

        internal static MeshImageAtlas Active => s_active ? s_active : null;

        /// <summary>Called by an atlas when it enables. False if another atlas is already active.</summary>
        internal static bool TryClaim(MeshImageAtlas atlas)
        {
            if (s_active && s_active != atlas && s_active.IsActive) return false;
            s_active = atlas;
            return true;
        }

        internal static void Release(MeshImageAtlas atlas)
        {
            if (s_active == atlas) s_active = null;
        }

        /// <summary>The active atlas; promotes an idle one or auto-spawns one if needed.</summary>
        internal static MeshImageAtlas GetOrSpawn()
        {
            if (IsQuitting) return null;
            if (s_active) return s_active;

            var found = FindSceneAtlas(includeIdle: true, out bool disabledExists);
            if (found)
            {
                // An idle duplicate takes over once the main atlas is gone.
                s_active = found;
                if (!found.IsActive) found.Activate();
                return found;
            }
            // A switched-off atlas means "no atlas for now", not "spawn
            // another"; its images wait until it is re-enabled.
            if (disabledExists) return null;

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

            var spawned = Object.Instantiate(prefab);
            spawned.MarkAutoSpawned();
            s_active = spawned;
            return spawned;
        }

        /// <summary>
        /// Non-spawning lookup of the atlas <paramref name="image"/> should use,
        /// for contexts where Instantiate is forbidden (OnValidate).
        /// </summary>
        internal static MeshImageAtlas ExistingFor(MeshImage image)
        {
            if (IsQuitting) return null;
#if UNITY_EDITOR
            // An image being edited in prefab mode uses an atlas inside that
            // prefab, if it has one. The stage scene can be unloaded
            // mid-transition, so guard against that.
            var stage = image != null ? PrefabStageUtility.GetPrefabStage(image.gameObject) : null;
            if (stage != null && stage.scene.IsValid() && stage.scene.isLoaded)
                foreach (var root in stage.scene.GetRootGameObjects())
                {
                    var found = root.GetComponentInChildren<MeshImageAtlas>(true);
                    if (found != null && found.IsActive) return found;
                }
#endif
            if (s_active) return s_active;
            s_active = FindSceneAtlas(includeIdle: false, out _);
            return s_active;
        }

        // Active atlases first; an enabled idle duplicate only when asked for.
        // disabledExists reports an atlas the user switched off (component or
        // GameObject), which suppresses auto-spawning a replacement.
        private static MeshImageAtlas FindSceneAtlas(bool includeIdle, out bool disabledExists)
        {
            MeshImageAtlas idle = null;
            disabledExists = false;
#if UNITY_EDITOR
            // FindObjectsByType skips DontSave objects, i.e. an edit-mode
            // auto-spawn. Without this, a lookup after domain reload misses it
            // and spawns a duplicate. Prefab-mode atlases serve only their prefab.
            foreach (var atlas in Resources.FindObjectsOfTypeAll<MeshImageAtlas>())
            {
                if (EditorUtility.IsPersistent(atlas) || !atlas.gameObject.scene.IsValid()) continue;
                if (EditorSceneManager.IsPreviewScene(atlas.gameObject.scene)) continue;
#else
            foreach (var atlas in Object.FindObjectsByType<MeshImageAtlas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
#endif
                // A retired atlas is on its way out (destroy is deferred); it
                // neither serves images nor counts as switched off.
                if (atlas.IsRetired) continue;
                if (!atlas.isActiveAndEnabled) { disabledExists = true; continue; }
                if (atlas.IsActive) return atlas;
                if (idle == null) idle = atlas;
            }
            return includeIdle ? idle : null;
        }

#if UNITY_EDITOR
        // OnApplicationQuit sets IsQuitting on every play-mode exit. Without
        // resetting it, the static survives the play→edit transition (no
        // domain reload by default) and every lookup returns null for the rest
        // of the edit session — silently breaking every MeshImage.
        [InitializeOnLoadMethod]
        private static void EditorInit()
        {
            IsQuitting = false;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode) IsQuitting = true;
            else if (state == PlayModeStateChange.EnteredEditMode) IsQuitting = false;
        }
#endif
    }
}
