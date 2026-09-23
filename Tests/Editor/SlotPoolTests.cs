using NUnit.Framework;
using UnityEngine;

namespace MeshImages.Tests
{
    public class SlotPoolTests
    {
        private GameObject _parent;
        private MeshImageSlotPool _pool;

        [SetUp]
        public void SetUp()
        {
            _parent = new GameObject("PoolParent") { hideFlags = HideFlags.HideAndDontSave };
            _pool = new MeshImageSlotPool();
            _pool.Build(_parent.transform, 2, 0);
        }

        [TearDown]
        public void TearDown()
        {
            _pool.Destroy();
            Object.DestroyImmediate(_parent);
        }

        [Test]
        public void Build_CreatesGridSquaredSlots()
        {
            Assert.AreEqual(4, _pool.Capacity);
            Assert.AreEqual(4, _parent.transform.childCount);
            Assert.IsFalse(_pool.NeedsRebuild(2));
            Assert.IsTrue(_pool.NeedsRebuild(3));
        }

        [Test]
        public void Acquire_FailsWhenFull()
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.IsTrue(_pool.TryAcquireSlot(out int slot));
                Assert.AreEqual(i, slot);
            }
            Assert.IsFalse(_pool.TryAcquireSlot(out int none));
            Assert.AreEqual(-1, none);
        }

        [Test]
        public void Release_MakesSlotReusable()
        {
            for (int i = 0; i < 4; i++) _pool.TryAcquireSlot(out _);
            _pool.ReleaseSlot(2);
            Assert.IsTrue(_pool.TryAcquireSlot(out int slot));
            Assert.AreEqual(2, slot);
        }

        [Test]
        public void Slots_AreHiddenFromCamerasUntilRendering()
        {
            var renderer = _pool[0].Renderer;
            Assert.IsTrue(renderer.forceRenderingOff);
            _pool.SetRenderingEnabled(true);
            Assert.IsFalse(renderer.forceRenderingOff);
            _pool.SetRenderingEnabled(false);
            Assert.IsTrue(renderer.forceRenderingOff);
        }

        [Test]
        public void Destroy_RemovesSlotObjects()
        {
            _pool.Destroy();
            Assert.AreEqual(0, _pool.Capacity);
            Assert.AreEqual(0, _parent.transform.childCount);
        }
    }
}
