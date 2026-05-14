using UnityEditor;
using UnityEditor.UI;

namespace MeshImages.Editor
{
    [CustomEditor(typeof(MeshImage), true)]
    [CanEditMultipleObjects]
    public class MeshImageEditor : RawImageEditor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject,
                "m_Script", "m_Texture", "m_Color", "m_RaycastTarget",
                "m_RaycastPadding", "m_Maskable", "m_Material",
                "m_OnCullStateChanged", "m_UVRect");
            serializedObject.ApplyModifiedProperties();
        }
    }
}