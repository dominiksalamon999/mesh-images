using UnityEngine;

namespace MeshImages
{
    internal static class MeshImageAtlasUtility
    {
        /// <summary>
        /// World-space AABB of a mesh after applying the given scale and rotation,
        /// computed from its local-bounds corners (no vertex iteration).
        /// </summary>
        public static Bounds ComputeRotatedScaledAabb(Mesh mesh, Vector3 scale, Quaternion rotation)
        {
            var b = mesh.bounds;
            Vector3 c = b.center, e = b.extents;
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;

            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + (((i & 1) == 0) ? +e.x : -e.x),
                    c.y + (((i & 2) == 0) ? +e.y : -e.y),
                    c.z + (((i & 4) == 0) ? +e.z : -e.z));
                var transformed = rotation * Vector3.Scale(corner, scale);
                min = Vector3.Min(min, transformed);
                max = Vector3.Max(max, transformed);
            }

            var result = new Bounds();
            result.SetMinMax(min, max);
            return result;
        }

        public static void DestroySafe(Object obj)
        {
            if (obj == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying) { Object.DestroyImmediate(obj); return; }
#endif
            Object.Destroy(obj);
        }
    }
}