using UnityEditor;
using UnityEditor.UI;
using UnityEngine;

namespace MeshImages.Editor
{
    // Lives in an Editor/ folder. Without this, RawImageEditor (registered with
    // editorForChildClasses: true) hijacks the inspector for any RawImage subclass
    // and the new SerializeField fields don't show up.
    [CustomEditor(typeof(MeshImage), true)]
    [CanEditMultipleObjects]
    public class MeshImageEditor : RawImageEditor
    {
        public override void OnInspectorGUI()
        {
            // Default RawImage inspector (texture, color, raycast target, etc.)
            // This call has its own Update/ApplyModifiedProperties internally.
            base.OnInspectorGUI();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            DrawPropertiesExcluding(serializedObject,
                "m_Script", "m_Texture", "m_Color", "m_RaycastTarget",
                "m_RaycastPadding", "m_Maskable", "m_Material",
                "m_OnCullStateChanged", "m_UVRect");
            if (EditorGUI.EndChangeCheck())
            {
                // Route undo through the non-buggy path. Unity's ApplyModifiedProperties
                // walks Vector3 fields through the Undo postprocess system, which trips
                // "Unsupported type Vector3f" on prefab instances. RegisterCompleteObjectUndo
                // uses a different code path that doesn't.
                foreach (var t in targets)
                    Undo.RegisterCompleteObjectUndo(t, "Edit MeshImage Preview");
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}
