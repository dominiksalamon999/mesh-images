using NUnit.Framework;
using UnityEngine;

namespace MeshImages.Tests
{
    public class SlotLayoutTests
    {
        private const float Eps = 1e-5f;

        private static void AreEqual(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, Eps);
            Assert.AreEqual(expected.y, actual.y, Eps);
            Assert.AreEqual(expected.z, actual.z, Eps);
        }

        [Test]
        public void UvRect_Slot0_IsTopLeftCell()
        {
            var cfg = new SlotLayoutConfig(4, 0f, 10f, 5f);
            var uv = MeshImageSlotPool.GetSlotUvRect(0, in cfg);
            Assert.AreEqual(0f, uv.x, Eps);
            Assert.AreEqual(0.75f, uv.y, Eps);
            Assert.AreEqual(0.25f, uv.width, Eps);
            Assert.AreEqual(0.25f, uv.height, Eps);
        }

        [Test]
        public void UvRect_LastSlot_IsBottomRightCell()
        {
            var cfg = new SlotLayoutConfig(4, 0f, 10f, 5f);
            var uv = MeshImageSlotPool.GetSlotUvRect(15, in cfg);
            Assert.AreEqual(0.75f, uv.x, Eps);
            Assert.AreEqual(0f, uv.y, Eps);
        }

        [Test]
        public void UvRect_PaddingShrinksCellOnAllSides()
        {
            var cfg = new SlotLayoutConfig(2, 0.1f, 10f, 5f);
            var uv = MeshImageSlotPool.GetSlotUvRect(0, in cfg);
            Assert.AreEqual(0.05f, uv.x, Eps);
            Assert.AreEqual(0.55f, uv.y, Eps);
            Assert.AreEqual(0.4f, uv.width, Eps);
            Assert.AreEqual(0.4f, uv.height, Eps);
        }

        [Test]
        public void SlotPosition_MatchesUvCell()
        {
            // 2x2 grid over a 10x10 view: slot 0 centre is top-left quadrant.
            var cfg = new SlotLayoutConfig(2, 0f, 100f, 5f);
            AreEqual(new Vector3(-2.5f, 2.5f, 100f), MeshImageSlotPool.GetSlotLocalPosition(0, Vector3.zero, in cfg));
            AreEqual(new Vector3(2.5f, -2.5f, 100f), MeshImageSlotPool.GetSlotLocalPosition(3, Vector3.zero, in cfg));
        }

        [Test]
        public void SlotPosition_AddsOffset()
        {
            var cfg = new SlotLayoutConfig(2, 0f, 100f, 5f);
            AreEqual(new Vector3(-1.5f, 2.5f, 99f),
                MeshImageSlotPool.GetSlotLocalPosition(0, new Vector3(1f, 0f, -1f), in cfg));
        }

        [Test]
        public void Aabb_RotatedCube_GrowsBySqrt2()
        {
            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var b = MeshImageAtlasUtility.ComputeRotatedScaledAabb(cube, Vector3.one, Quaternion.Euler(0f, 0f, 45f));
            Assert.AreEqual(Mathf.Sqrt(2f), b.size.x, 1e-4f);
            Assert.AreEqual(Mathf.Sqrt(2f), b.size.y, 1e-4f);
            Assert.AreEqual(1f, b.size.z, 1e-4f);
        }

        [Test]
        public void Aabb_AppliesScaleBeforeRotation()
        {
            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var b = MeshImageAtlasUtility.ComputeRotatedScaledAabb(cube, new Vector3(2f, 1f, 1f), Quaternion.Euler(0f, 0f, 90f));
            Assert.AreEqual(1f, b.size.x, 1e-4f);
            Assert.AreEqual(2f, b.size.y, 1e-4f);
        }
    }
}
