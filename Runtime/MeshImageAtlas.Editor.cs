#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using static MeshImages.MeshImageAtlasUtility;

namespace MeshImages
{
    /// <summary>
    /// Editor-only half of <see cref="MeshImageAtlas"/>: editor lifecycle hooks,
    /// deferred-render scheduling, and scene/play-mode cleanup. The entire file
    /// is gated by UNITY_EDITOR, so nothing here ships in player builds.
    /// </summary>
    public partial class MeshImageAtlas
    {
        // ---------------- Editor-only state ----------------
        private bool _editorRenderQueued;

        // ---------------- Instance hooks ----------------
        // Single sub/unsub point. on=true subscribes (after a defensive unsub);
        // on=false just unsubscribes everything this instance attached.
        private void ToggleEditorHooks(bool on)
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update -= EditorRenderOnce;
            EditorSceneManager.sceneOpened -= OnEditorSceneOpened;
            EditorSceneManager.sceneClosing -= OnEditorSceneClosing;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            if (!on) return;

            EditorApplication.update += OnEditorUpdate;
            EditorApplication.delayCall += EditorRegisterAllImages;
            EditorSceneManager.sceneOpened += OnEditorSceneOpened;
            // Auto-spawned atlases must be destroyed before scene close and
            // before play-mode entry, or Unity logs "Some objects were not
            // cleaned up when closing the scene."
            EditorSceneManager.sceneClosing += OnEditorSceneClosing;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnEditorUpdate()
        {
            if (this == null) { EditorApplication.update -= OnEditorUpdate; return; }
            if (Application.isPlaying) return;
            EnsureLayout();
            ProcessPendingRetries();
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
            if (atlasCamera == null || _atlasTexture == null) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            TryRender(EditorApplication.timeSinceStartup);
            // Repaint only the views that can show the atlas, right away.
            SceneView.RepaintAll();
            RepaintGameViews();
        }

        private static System.Type s_gameViewType;

        // GameView is internal; fall back to repainting everything if it moves.
        private static void RepaintGameViews()
        {
            s_gameViewType ??= typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (s_gameViewType == null) { UnityEditorInternal.InternalEditorUtility.RepaintAllViews(); return; }
            foreach (var view in Resources.FindObjectsOfTypeAll(s_gameViewType))
                ((EditorWindow)view).Repaint();
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

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (_autoSpawned && state == PlayModeStateChange.ExitingEditMode)
                DestroySafe(gameObject);
        }

        private void EditorRegisterAllImages()
        {
            if (this == null || Application.isPlaying) return;
            var images = Object.FindObjectsByType<MeshImage>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var img in images)
                if (img != null && img.isActiveAndEnabled) img.Reregister();
        }
    }
}
#endif
