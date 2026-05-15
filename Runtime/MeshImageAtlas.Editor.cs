#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using static MeshImages.MeshImageAtlasUtility;

namespace MeshImages
{
    /// <summary>
    /// Editor-only half of <see cref="MeshImageAtlas"/>: editor lifecycle hooks,
    /// deferred-render scheduling, scene/play-mode cleanup, and the static
    /// quit-flag reset. The entire file is gated by UNITY_EDITOR, so nothing
    /// here ships in player builds.
    /// </summary>
    public partial class MeshImageAtlas
    {
        // ---------------- Editor-only state ----------------
        private bool _editorRenderQueued, _deactivateQueued;

        // ---------------- Static init ----------------
        // OnApplicationQuit sets _isQuitting on every play-mode exit. Without
        // resetting it, the static survives the play→edit transition (no
        // domain reload by default) and Instance returns null for the rest of
        // the edit session — silently breaking every MeshImage.
        [InitializeOnLoadMethod]
        private static void EditorInit()
        {
            _isQuitting = false;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChangedStatic;
            EditorApplication.playModeStateChanged += OnPlayModeStateChangedStatic;
        }

        private static void OnPlayModeStateChangedStatic(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode) _isQuitting = true;
            else if (state == PlayModeStateChange.EnteredEditMode) _isQuitting = false;
        }

        // ---------------- Instance hooks ----------------
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
            _pool.DeactivateQueued -= OnPoolDeactivateQueued;
            if (!on) return;

            EditorApplication.update += OnEditorUpdate;
            EditorApplication.delayCall += EditorRegisterAllImages;
            EditorSceneManager.sceneOpened += OnEditorSceneOpened;
            // Auto-spawned atlases must be destroyed before scene close and
            // before play-mode entry, or Unity logs "Some objects were not
            // cleaned up when closing the scene."
            EditorSceneManager.sceneClosing += OnEditorSceneClosing;
            EditorApplication.playModeStateChanged += OnPlayModeStateChangedInstance;
            // Edit-mode only: LateUpdate doesn't run, so when the pool defers a
            // deactivate, we must hop an EditorApplication.update tick to drain.
            _pool.DeactivateQueued += OnPoolDeactivateQueued;
        }

        private void OnPoolDeactivateQueued()
        {
            if (Application.isPlaying || _deactivateQueued) return;
            _deactivateQueued = true;
            EditorApplication.QueuePlayerLoopUpdate();
            EditorApplication.update += FlushPendingDeactivates;
        }

        private void OnEditorUpdate()
        {
            if (this == null) { EditorApplication.update -= OnEditorUpdate; return; }
            if (Application.isPlaying) return;
            EnsurePoolMatchesGrid();
            _pool.DrainPendingDeactivates();
            TryRender(EditorApplication.timeSinceStartup);
        }

        // ---------------- Deferred render ----------------
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
            _pool.DrainPendingDeactivates();
        }

        // ---------------- Scene / play-mode cleanup ----------------
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
    }
}
#endif