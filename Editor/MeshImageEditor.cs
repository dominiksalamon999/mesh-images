using UnityEditor;
using UnityEditor.UI;

namespace MeshImages.Editor
{
    [CustomEditor(typeof(MeshImage), true)]
    [CanEditMultipleObjects]
    public class MeshImageEditor : RawImageEditor
    {
        // Texture, UV rect and UI material are driven by the atlas; everything
        // else (Color, Raycast Target, Maskable, mesh settings) stays editable.
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject,
                "m_Script", "m_Texture", "m_Material",
                "m_OnCullStateChanged", "m_UVRect", "m_RaycastPadding");
            serializedObject.ApplyModifiedProperties();
        }
    }
}
