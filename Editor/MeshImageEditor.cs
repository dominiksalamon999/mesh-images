using UnityEditor;
using UnityEditor.UI;
using UnityEngine;

namespace MeshImages.Editor
{
    [CustomEditor(typeof(MeshImage), true)]
    [CanEditMultipleObjects]
    public class MeshImageEditor : RawImageEditor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            DrawPropertiesExcluding(serializedObject,
                "m_Script", "m_Texture", "m_Color", "m_RaycastTarget",
                "m_RaycastPadding", "m_Maskable", "m_Material",
                "m_OnCullStateChanged", "m_UVRect");

            if (EditorGUI.EndChangeCheck())
            {
                foreach (var t in targets)
                    Undo.RegisterCompleteObjectUndo(t, "Edit MeshImage");

                serializedObject.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}