using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MeshImages.Tests
{
    // GPU tests: render the atlas and read pixels back.
    public class AtlasRenderTests
    {
        private MeshImageAtlas _atlas;
        private GameObject _canvas;

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
            _atlas = MeshImageAtlas.Instance;
            _canvas = new GameObject("Canvas", typeof(Canvas));
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_canvas);
            foreach (var atlas in Resources.FindObjectsOfTypeAll<MeshImageAtlas>())
                if (!EditorUtility.IsPersistent(atlas))
                    Object.DestroyImmediate(atlas.gameObject);
        }

        private MeshImage CreateImage(Vector3 rotation)
        {
            var go = new GameObject("Img", typeof(RectTransform));
            go.transform.SetParent(_canvas.transform, false);
            var img = go.AddComponent<MeshImage>();
            img.Configure(mesh: Resources.GetBuiltinResource<Mesh>("Cube.fbx"), rotation: rotation);
            return img;
        }

        private static int CellOf(MeshImage img, int gridSize)
        {
            var uv = img.SlotUv;
            int col = Mathf.FloorToInt((uv.x + uv.width * 0.5f) * gridSize);
            int rowFromBottom = Mathf.FloorToInt((uv.y + uv.height * 0.5f) * gridSize);
            return (gridSize - 1 - rowFromBottom) * gridSize + col;
        }

        private static Color32[] ReadRegion(RenderTexture rt, Rect uv)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            int x = Mathf.RoundToInt(uv.x * rt.width), y = Mathf.RoundToInt(uv.y * rt.height);
            int w = Mathf.RoundToInt(uv.width * rt.width), h = Mathf.RoundToInt(uv.height * rt.height);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(x, y, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var pixels = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return pixels;
        }

        private static void Clear(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.clear);
            RenderTexture.active = prev;
        }

        private static int CountOpaque(Color32[] px)
        {
            int n = 0;
            foreach (var c in px) if (c.a > 128) n++;
            return n;
        }

        [Test]
        public void AtlasTexture_IsGridTimesCellResolution()
        {
            var img = CreateImage(Vector3.zero);
            var so = new SerializedObject(_atlas);
            int grid = so.FindProperty("gridSize").intValue;
            so.FindProperty("cellResolution").intValue = 64;
            so.ApplyModifiedPropertiesWithoutUndo();

            _atlas.EnsureLayout();

            Assert.AreEqual(grid * 64, _atlas.Texture.width);
            Assert.IsTrue(img.IsPlaced);
            Assert.AreSame(_atlas.Texture, img.mainTexture, "images must follow the rebuilt texture");
        }

        [Test]
        public void CellRender_MatchesFullRender()
        {
            var img = CreateImage(new Vector3(30f, 45f, 0f));
            int grid = new SerializedObject(_atlas).FindProperty("gridSize").intValue;
            var rt = _atlas.Texture;

            _atlas.RenderFull();
            var full = ReadRegion(rt, img.SlotUv);
            Assert.Greater(CountOpaque(full), full.Length / 10, "full render should draw the cube");

            Clear(rt);
            _atlas.RenderCells(new[] { CellOf(img, grid) });
            var cell = ReadRegion(rt, img.SlotUv);

            int diff = 0;
            for (int i = 0; i < full.Length; i++)
            {
                var a = full[i]; var b = cell[i];
                if (Mathf.Abs(a.r - b.r) > 8 || Mathf.Abs(a.g - b.g) > 8 ||
                    Mathf.Abs(a.b - b.b) > 8 || Mathf.Abs(a.a - b.a) > 8) diff++;
            }
            Assert.Less(diff, full.Length / 100, "cell render should match the full render within 1%");
        }

        [Test]
        public void CellRender_LeavesOtherCellsUntouched()
        {
            var a = CreateImage(new Vector3(0f, 10f, 0f));
            var b = CreateImage(new Vector3(0f, 20f, 0f));
            int grid = new SerializedObject(_atlas).FindProperty("gridSize").intValue;
            var rt = _atlas.Texture;

            Clear(rt);
            _atlas.RenderCells(new[] { CellOf(a, grid) });

            Assert.Greater(CountOpaque(ReadRegion(rt, a.SlotUv)), 0);
            Assert.AreEqual(0, CountOpaque(ReadRegion(rt, b.SlotUv)));
        }
    }
}
