using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MeshImages.Tests
{
    // Runs in a fresh empty scene; the test runner restores the open scenes afterwards.
    public class AtlasRegistrationTests
    {
        private Mesh _cube;
        private MeshImageAtlas _atlas;
        private GameObject _canvas;

        // NewScene(Single) discards unsaved edits without asking, and the runner's
        // save prompt doesn't show for CLI/automated runs, so refuse instead.
        [OneTimeSetUp]
        public void RequireSavedScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty)
                    Assert.Ignore("Open scenes have unsaved changes; save them before running these tests.");
        }

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            _atlas = MeshImageAtlas.Instance;
            Assert.IsNotNull(_atlas, "atlas should auto-spawn in edit mode");
            _canvas = new GameObject("Canvas", typeof(Canvas));
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_canvas);
            foreach (var atlas in Resources.FindObjectsOfTypeAll<MeshImageAtlas>())
                if (!UnityEditor.EditorUtility.IsPersistent(atlas))
                    Object.DestroyImmediate(atlas.gameObject);
        }

        private MeshImage CreateImage(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_canvas.transform, false);
            return go.AddComponent<MeshImage>();
        }

        private static MeshImageAtlas SpawnSecondAtlas()
            => Object.Instantiate(Resources.Load<MeshImageAtlas>("MeshImageAtlas"));

        [Test]
        public void AutoSpawnedAtlas_IsNotSavedWithScene()
        {
            Assert.AreNotEqual(0, (int)(_atlas.gameObject.hideFlags & HideFlags.DontSaveInEditor));
        }

        [Test]
        public void NewImage_DefaultsToUnitScale_AndIsNotPlacedWithoutMesh()
        {
            var img = CreateImage("A");
            Assert.AreEqual(Vector3.one, img.Scale);
            Assert.IsFalse(img.IsPlaced);
            Assert.AreEqual(0, _atlas.UsedSlotCount);
        }

        [Test]
        public void SettingMesh_PlacesImage()
        {
            var img = CreateImage("A");
            img.Mesh = _cube;
            Assert.IsTrue(img.IsPlaced);
            Assert.AreSame(_atlas.Texture, img.mainTexture);
            Assert.AreEqual(1, _atlas.UsedSlotCount);
        }

        // Placement is runtime state; a scene save must not record which slot an image got.
        [Test]
        public void Placement_LeavesSerializedUvRectAndTexture_Untouched()
        {
            var img = CreateImage("A");
            img.Mesh = _cube;
            Assert.IsTrue(img.IsPlaced);
            Assert.AreNotEqual(new Rect(0f, 0f, 1f, 1f), img.SlotUv);
            Assert.AreEqual(new Rect(0f, 0f, 1f, 1f), img.uvRect);
            Assert.IsNull(img.texture);
        }

        [Test]
        public void ClearingMesh_HidesImage_AndFreesSlot()
        {
            var img = CreateImage("A");
            img.Mesh = _cube;
            img.Mesh = null;
            Assert.IsFalse(img.IsPlaced);
            Assert.AreEqual(0, _atlas.UsedSlotCount);
        }

        [Test]
        public void IdenticalImages_ShareOneSlot()
        {
            var a = CreateImage("A");
            var b = CreateImage("B");
            a.Mesh = _cube;
            b.Mesh = _cube;
            Assert.AreEqual(2, _atlas.ImageCount);
            Assert.AreEqual(1, _atlas.UsedSlotCount);
            Assert.AreEqual(a.SlotUv, b.SlotUv);

            b.Rotation = new Vector3(0f, 45f, 0f);
            Assert.AreEqual(2, _atlas.UsedSlotCount);
            Assert.AreNotEqual(a.SlotUv, b.SlotUv);
        }

        [Test]
        public void SharedSlot_SurvivesUntilLastImageLeaves()
        {
            var a = CreateImage("A");
            var b = CreateImage("B");
            a.Mesh = _cube;
            b.Mesh = _cube;

            a.enabled = false;
            Assert.AreEqual(1, _atlas.UsedSlotCount);
            Assert.IsTrue(b.IsPlaced);

            b.enabled = false;
            Assert.AreEqual(0, _atlas.UsedSlotCount);
        }

        [Test]
        public void DisabledImage_HoldsNoSlot()
        {
            var img = CreateImage("A");
            img.enabled = false;
            img.Mesh = _cube;
            Assert.IsFalse(img.IsPlaced);
            Assert.AreEqual(0, _atlas.ImageCount);
        }

        [Test]
        public void DisablingAtlas_HidesImages_AndReenablingRestoresThem()
        {
            var img = CreateImage("A");
            img.Mesh = _cube;

            _atlas.enabled = false;
            Assert.IsFalse(img.IsPlaced);

            _atlas.enabled = true;
            Assert.IsTrue(img.IsPlaced);
            Assert.AreSame(_atlas.Texture, img.mainTexture);
        }

        [Test]
        public void SecondAtlas_StaysIdleInEditMode()
        {
            LogAssert.Expect(LogType.Warning, new Regex("stays idle"));
            var second = SpawnSecondAtlas();

            Assert.IsTrue(second != null, "second atlas must not be destroyed");
            Assert.IsNull(second.Texture);
            Assert.AreSame(_atlas, MeshImageAtlas.Instance);
        }

        [Test]
        public void IdleAtlas_TakesOver_WhenMainAtlasIsDestroyed()
        {
            var img = CreateImage("A");
            img.Mesh = _cube;
            LogAssert.Expect(LogType.Warning, new Regex("stays idle"));
            var second = SpawnSecondAtlas();

            Object.DestroyImmediate(_atlas.gameObject);
            Assert.IsFalse(img.IsPlaced);

            Assert.AreSame(second, MeshImageAtlas.Instance);
            Assert.IsNotNull(second.Texture);

            second.ProcessPendingRetries();
            Assert.IsTrue(img.IsPlaced);
            Assert.AreSame(second.Texture, img.mainTexture);
        }

        [Test]
        public void InspectorEdit_ReachesAtlasImmediately_InEditMode()
        {
            var a = CreateImage("A");
            var b = CreateImage("B");
            a.Mesh = _cube;
            b.Mesh = _cube;
            Assert.AreEqual(1, _atlas.UsedSlotCount);

            // Same path as dragging a value in the inspector: serialized edit + OnValidate.
            var so = new UnityEditor.SerializedObject(b);
            so.FindProperty("rotation").vector3Value = new Vector3(0f, 30f, 0f);
            so.ApplyModifiedPropertiesWithoutUndo();

            // No editor tick or delayCall has run yet; the change must already be placed.
            Assert.AreEqual(2, _atlas.UsedSlotCount);
            Assert.AreNotEqual(a.SlotUv, b.SlotUv);
        }

        [Test]
        public void DestroyedMesh_StillFreesItsSlot()
        {
            var mesh = Object.Instantiate(_cube);
            var img = CreateImage("A");
            img.Mesh = mesh;
            Assert.AreEqual(1, _atlas.UsedSlotCount);

            Object.DestroyImmediate(mesh);
            img.enabled = false;
            Assert.AreEqual(0, _atlas.UsedSlotCount);
        }

        [Test]
        public void NearlyEqualRotations_DoNotShareASlot()
        {
            var a = CreateImage("A");
            var b = CreateImage("B");
            a.Configure(mesh: _cube, rotation: new Vector3(10f, 0f, 0f));
            b.Configure(mesh: _cube, rotation: new Vector3(10.000001f, 0f, 0f));
            Assert.AreEqual(2, _atlas.UsedSlotCount);
        }

        [Test]
        public void ImageRejectedByFullAtlas_IsPlacedWhenASlotFrees()
        {
            int capacity = 49; // prefab gridSize 7
            var images = new MeshImage[capacity + 1];
            for (int i = 0; i < images.Length; i++)
            {
                images[i] = CreateImage($"I{i}");
                images[i].Configure(mesh: _cube, rotation: new Vector3(0f, i, 0f));
            }
            var last = images[capacity];
            Assert.IsFalse(last.IsPlaced);

            images[0].enabled = false;
            _atlas.ProcessPendingRetries();
            Assert.IsTrue(last.IsPlaced);
        }

        [Test]
        public void DisabledAtlas_BlocksAutoSpawn_AndPlacesWaitingImagesWhenReenabled()
        {
            _atlas.enabled = false;
            var img = CreateImage("A");
            img.Mesh = _cube;
            Assert.IsFalse(img.IsPlaced);
            Assert.IsNull(MeshImageAtlas.Instance, "must not spawn a second atlas");

            _atlas.enabled = true;
            _atlas.ProcessPendingRetries();
            Assert.IsTrue(img.IsPlaced);
        }

        [Test]
        public void AtlasThatGoesIdle_HandsItsImagesToTheActiveAtlas()
        {
            var img = CreateImage("A");
            img.Mesh = _cube;
            LogAssert.Expect(LogType.Warning, new Regex("stays idle"));
            var second = SpawnSecondAtlas();

            // Main switched off; the idle one gets promoted (e.g. by the editor poll).
            _atlas.enabled = false;
            Assert.AreSame(second, MeshImageAtlas.Instance);

            // Re-enabling the original makes it the idle one; its image must move over.
            _atlas.enabled = true;
            second.ProcessPendingRetries();
            Assert.IsTrue(img.IsPlaced);
            Assert.AreSame(second.Texture, img.mainTexture);
        }
    }
}
