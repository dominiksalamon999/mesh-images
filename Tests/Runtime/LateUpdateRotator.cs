using UnityEngine;

namespace MeshImages.Tests
{
    // Changes its MeshImage once, from a LateUpdate that runs after the atlas's.
    [DefaultExecutionOrder(10000)]
    internal class LateUpdateRotator : MonoBehaviour
    {
        public bool Done;

        private void LateUpdate()
        {
            if (Done) return;
            GetComponent<MeshImage>().Rotation = new Vector3(0f, 45f, 0f);
            Done = true;
        }
    }
}
