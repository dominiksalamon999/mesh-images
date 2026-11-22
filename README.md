# Mesh Images

Render 3D meshes inside Unity UI. Each `MeshImage` is drawn into one shared atlas texture, so many 3D icons cost one texture and one camera.

Good for inventories, item previews, editor tooling and other UI that shows 3D objects.

## Requirements

- Unity 6 (6000.0) or newer
- The Universal Render Pipeline (URP). See [Render pipelines](#render-pipelines).

## Installation

In Unity, open **Window > Package Manager**, click **+ > Add package from git URL**, and enter:

```
https://github.com/dominiksalamon999/mesh-images.git
```

Or add this line to `dependencies` in `Packages/manifest.json`:

```json
"com.dominiksalamon999.mesh-images": "https://github.com/dominiksalamon999/mesh-images.git"
```

## Usage

1. Add a **MeshImage** component to a GameObject under a Canvas.
2. Assign a **Mesh**. Optionally set a **Mesh Material**, **Translation**, **Rotation** and **Scale**.

That's it. When no atlas exists, one is created from the package's `MeshImageAtlas` prefab. In edit mode it isn't saved with the scene.

From code, set the properties (`Mesh`, `MeshMaterial`, `Translation`, `Rotation`, `Scale`, `PreserveAspect`), or call `Configure(...)` to change several at once with a single update. Setting `Mesh` to null hides the image.

Images with the same mesh, material, translation, rotation and scale share one atlas cell.

### Preserve aspect

Atlas cells are square. A MeshImage that isn't square stretches its cell to fit, unless **Preserve Aspect** is on.

### Configuring the atlas

To change atlas settings, put the `MeshImageAtlas` prefab (from `Runtime/Resources`) in your first scene and edit it there. An atlas you place always replaces an auto-created one. In play mode the atlas survives scene loads. Keep one atlas per game: in play mode a second placed atlas is destroyed with a warning, and in edit mode it stays idle until the first one goes away.

| Setting | What it does |
| --- | --- |
| Default Material | Used by MeshImages without their own material. The package ships a URP Lit material. |
| Grid Size | Cells per row and column, up to 64. The atlas holds `gridSize × gridSize` different images. |
| Cell Resolution | Pixels per cell. The atlas texture is `gridSize × cellResolution` pixels square. |
| Object Fill Fraction | How much of its cell a mesh fills. |
| UV Padding Fraction | Empty border inside each cell, as a fraction of the cell. |
| Preview Depth | How far in front of the atlas camera the meshes sit. It must be between the camera's near and far clip planes. |
| Auto Fit / Auto Center | Scale each mesh to its cell and center it. |
| Render Fps | 0 renders only when something changes. Raise it for animated materials or changing lights. |

Changes to these settings, and to the atlas camera's size, clip planes and background, apply right away to the images already shown.

### Layer

The atlas camera draws a single layer: the lowest layer in its culling mask. The prefab uses layer 31, which Unity leaves unnamed. Anything else on that layer in front of the atlas camera also shows up in the icons, so keep that layer free, or choose another one through the camera's culling mask.

### Lighting

Icons are lit by the lights in the open scenes, like any other object. With **Render Fps** at 0 they re-render only when an image or atlas setting changes, so a lighting change shows up only after the next such change.

## Render pipelines

The package is built for URP. It renders through URP render requests and turns off URP features the atlas doesn't need, such as shadows and post-processing. The default material uses the URP Lit shader.

Other pipelines fall back to `Camera.Render()`. Use a placed atlas and assign a default material made for your pipeline.

## Samples

**Basic Example** (Package Manager > Mesh Images > Samples) has a scene with a few MeshImages. It uses URP and the Input System package.

## Tests

The package has EditMode and PlayMode tests. To see them in the Test Runner of a project that installs the package from git, add `"testables": ["com.dominiksalamon999.mesh-images"]` to its `Packages/manifest.json`. The render tests need a GPU, so they can't run with `-nographics`.
