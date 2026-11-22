using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MeshImages.Tests
{
    // Play-mode behaviour: which atlas wins, and when the atlas renders.
    public class AtlasPlayModeTests
    {
        private Mesh _cube;
        private GameObject _canvas;

        [SetUp]
        public void SetUp()
        {
            _cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            _canvas = new GameObject("Canvas", typeof(Canvas));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Object.Destroy(_canvas);
            foreach (var atlas in Object.FindObjectsByType<MeshImageAtlas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Object.Destroy(atlas.gameObject);
            yield return null;
        }

        private MeshImage CreateImage()
        {
            var go = new GameObject("Img", typeof(RectTransform));
            go.transform.SetParent(_canvas.transform, false);
            var img = go.AddComponent<MeshImage>();
            img.Mesh = _cube;
            return img;
        }

        private static MeshImageAtlas PlaceAtlas()
            => Object.Instantiate(Resources.Load<MeshImageAtlas>("MeshImageAtlas"));

        [UnityTest]
        public IEnumerator PlacedAtlas_ReplacesAutoSpawnedAtlas()
        {
            var img = CreateImage();
            var auto = MeshImageAtlas.Instance;
            Assert.IsTrue(img.IsPlaced, "play mode spawns an atlas on demand");

            // Like loading a scene that has its own atlas.
            var placed = PlaceAtlas();
            Assert.AreSame(placed, MeshImageAtlas.Instance);
            yield return null;

            Assert.IsTrue(auto == null, "the auto-spawned atlas is destroyed");
            Assert.IsTrue(img.IsPlaced);
            Assert.AreSame(placed.Texture, img.mainTexture);
        }

        [UnityTest]
        public IEnumerator SecondPlacedAtlas_IsDestroyed_WithAWarning()
        {
            var first = PlaceAtlas();
            LogAssert.Expect(LogType.Warning, new Regex("so this one is destroyed"));
            var second = PlaceAtlas();
            yield return null;

            Assert.IsTrue(second == null);
            Assert.AreSame(first, MeshImageAtlas.Instance);
        }

        [UnityTest]
        public IEnumerator ChangeMadeInLateUpdate_IsRenderedBeforeTheCanvasDraws()
        {
            var img = CreateImage();
            var atlas = MeshImageAtlas.Instance;
            yield return null;

            var driver = img.gameObject.AddComponent<LateUpdateRotator>();
            // Subscribed after the atlas, so this runs right after the atlas's handler.
            bool? pendingAtCanvasRender = null;
            void Check()
            {
                if (driver.Done && pendingAtCanvasRender == null)
                    pendingAtCanvasRender = atlas.HasPendingRender;
            }
            Canvas.willRenderCanvases += Check;
            try
            {
                for (int i = 0; i < 3 && pendingAtCanvasRender == null; i++)
                    yield return null;
            }
            finally
            {
                Canvas.willRenderCanvases -= Check;
            }

            Assert.AreEqual(false, pendingAtCanvasRender,
                            "a change made in LateUpdate must be rendered in the same frame");
        }
    }
}
