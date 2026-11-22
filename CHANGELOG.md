# Changelog

## [0.2.0] - 2026-11-22

### Added
- `MeshImage.PreserveAspect` (**Preserve Aspect** in the inspector) fits the square atlas cell inside a non-square rect instead of stretching it.
- MeshImages driven by an Animator update when the animation changes their mesh, material, translation, rotation or scale.
- PlayMode tests for which atlas wins and for render timing, plus EditMode tests for the fixes below.

### Changed
- **Breaking:** `MeshImageAtlas.Add` and `MeshImageAtlas.Remove` are internal. Calling them directly left the image out of sync with the atlas; `MeshImage` registers itself.
- The atlas prefab's default material is now `Runtime/Materials/MeshImageDefault.mat` (URP Lit).
- The atlas prefab's camera renders layer 31 instead of UI, so world-space UI near the atlas no longer shows up in the icons. Atlases already saved in scenes keep their layer.
- An atlas placed in a scene now replaces an auto-spawned one, in edit and play mode. In play mode, a second placed atlas is destroyed with a warning instead of silently.
- In play mode the atlas renders from `Canvas.willRenderCanvases`, after every script's `LateUpdate`, so changes made in `LateUpdate` show in the same frame.
- Grid size is limited to 64.
- The setup warnings (no default material, camera not orthographic, preview depth outside the clip range) are checked again whenever settings change, and logged only when they change.
- Rendering reuses one render request instead of allocating one per render, and no longer boxes the dirty-cell enumerator.
- README rewritten: usage, atlas settings, layer, lighting and render pipelines.

### Fixed
- A MeshImage's slot rect and the atlas texture were saved into scenes and prefabs (RawImage's `m_UVRect` and `m_Texture`), so a save recorded whichever slot the image happened to get.
- In a project without the imported sample, MeshImages without their own material showed nothing: the prefab's default material lived in `Samples~`, which Unity never imports. An image that can't be placed for lack of a material now logs a warning.
- Changing atlas settings (fill, padding, preview depth, auto fit, auto center, default material) or the atlas camera's size, clip planes or background didn't update images already shown.
- Changing grid size and cell resolution together, with the atlas width staying the same, left the per-cell texture at its old size.
- Images that didn't fit after a layout change stayed hidden even after space freed up. They now wait and are placed once they fit.
- An image whose mesh was cleared while it waited stayed on the waiting list, so the editor could spawn an atlas for nothing.
- The sample scene stored an old slot rect.

## [0.1.0] - 2026-09-23

### Added
- `MeshImage` properties `Mesh`, `MeshMaterial`, `Translation`, `Rotation`, `Scale`. Setting `Mesh` to null clears the image.
- `MeshImageAtlas.Add` returns whether the image was placed; `MeshImageAtlas.Remove` is now public.
- `cellResolution` setting on the atlas (default 220 px). The atlas texture is `gridSize × cellResolution` square and is rebuilt when either changes.
- When 8 or fewer images change in a frame, only their cells are re-rendered (each into a small texture copied into place) instead of the whole atlas.
- Images with the same mesh, material, translation, rotation and scale share one atlas slot.
- Inspector shows Color, Raycast Target and Maskable.
- EditMode tests.

### Changed
- Minimum Unity version is now 6000.0.
- `renderFps` defaults to 0: the atlas renders only when something changes. Raise it for animated materials or lighting.
- The atlas camera renders a single layer (the lowest in its culling mask); a wider mask is narrowed with a one-time warning.
- The atlas camera skips HDR, MSAA, shadows, depth/opaque texture copies and post-processing (applied in code under URP, and in the prefab and sample).
- Slot GameObjects stay active; empty slots just have no mesh. Removes the deferred-deactivate queue.
- Freeing a slot no longer triggers a re-render.
- In the editor, an atlas render repaints only the Scene and Game views instead of every editor window.
- Internal: atlas lookup and active/idle/disabled state moved into `AtlasRegistry`; the atlas's small value types moved to `MeshImageAtlas.Types.cs`.
- The serialized `material` field is renamed to `meshMaterial`; existing scenes and prefabs keep their value.
- A second atlas in edit mode now stays idle with a warning instead of being destroyed. An atlas inside a prefab being edited serves only that prefab.

### Fixed
- Preview meshes no longer show up in other cameras.
- Edit-mode auto-spawned atlases are no longer saved into scenes, and are still cleaned up after a script reload.
- New MeshImages defaulted to a zero scale and rendered nothing.
- An image with no mesh, or one that didn't fit in the atlas, displayed the whole atlas; a removed image kept showing its old slot.
- MeshImages in prefab assets and disabled MeshImages took atlas slots.
- Disabling the atlas left it as the active instance.
- Fewer "not allowed during OnValidate" hazards: inspector edits re-place the image right away, and wait for the next editor tick only when the atlas has to rebuild its layout.
- The "render requests not supported" warning is logged once instead of every frame.
- Images that couldn't be placed (no atlas yet, atlas full, atlas switched off) now wait and are placed as soon as an atlas activates or a slot frees, in the editor and in builds. The "atlas is full" warning is logged once per image.
- A switched-off atlas no longer causes a second one to be auto-spawned; its images come back when it is re-enabled.
- Destroying a mesh that an image still used no longer leaked its atlas slot.
- The editor looks for a missing atlas a few times per second in one place, instead of per image every editor tick.
- An empty atlas no longer re-renders on idle ticks.
- README install line used the wrong package name.
