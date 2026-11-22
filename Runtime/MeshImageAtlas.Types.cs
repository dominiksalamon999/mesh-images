using System;
using UnityEngine;

namespace MeshImages
{
    // Small value types used by MeshImageAtlas bookkeeping.
    public partial class MeshImageAtlas
    {
        // What one image asked for, plus the slot it got.
        private struct AtlasEntry
        {
            public int Slot;
            public Mesh Mesh;
            // The image's own material (may be null) and the one actually drawn,
            // so a defaultMaterial change can re-resolve on re-place.
            public Material RequestedMaterial, Material;
            public Vector3 Translation, Rotation, Scale;

            public SlotKey Key => new(Mesh, Material, Translation, Rotation, Scale);
        }

        // Images with an identical key render the same pixels, so they share one slot.
        private readonly struct SlotKey : IEquatable<SlotKey>
        {
            private readonly Mesh _mesh;
            private readonly Material _material;
            private readonly Vector3 _translation, _rotation, _scale;

            public SlotKey(Mesh mesh, Material material, Vector3 translation, Vector3 rotation, Vector3 scale)
            {
                _mesh = mesh; _material = material;
                _translation = translation; _rotation = rotation; _scale = scale;
            }

            // Exact Vector3.Equals (not the tolerant ==) so Equals agrees with GetHashCode.
            public bool Equals(SlotKey o) =>
                ReferenceEquals(_mesh, o._mesh) && ReferenceEquals(_material, o._material) &&
                _translation.Equals(o._translation) && _rotation.Equals(o._rotation) && _scale.Equals(o._scale);

            public override bool Equals(object obj) => obj is SlotKey o && Equals(o);

            public override int GetHashCode() => HashCode.Combine(
                IdOf(_mesh), IdOf(_material), _translation, _rotation, _scale);

            // Plain reference check, not Unity's overloaded null: a destroyed
            // mesh must keep its hash or Remove can't find (and free) its slot.
            private static int IdOf(UnityEngine.Object o) => ReferenceEquals(o, null) ? 0 : o.GetInstanceID();
        }

        private struct SharedSlot { public int Slot; public int RefCount; }

        // Every atlas and camera setting, besides grid size and cell resolution,
        // that decides where a slot sits or what it draws. When any of them
        // changes, all images are re-placed and the atlas re-renders.
        private readonly struct PlacementSettings : IEquatable<PlacementSettings>
        {
            private readonly Material _defaultMaterial;
            private readonly float _fill, _padding, _depth, _halfExtent, _near, _far;
            private readonly bool _autoFit, _autoCenter, _orthographic;
            private readonly Color _background;

            public PlacementSettings(Material defaultMaterial, float fill, float padding, float depth,
                                     bool autoFit, bool autoCenter, Camera camera)
            {
                _defaultMaterial = defaultMaterial;
                _fill = fill; _padding = padding; _depth = depth;
                _autoFit = autoFit; _autoCenter = autoCenter;
                bool hasCamera = camera != null;
                _halfExtent = hasCamera ? camera.orthographicSize : 0f;
                _near = hasCamera ? camera.nearClipPlane : 0f;
                _far = hasCamera ? camera.farClipPlane : 0f;
                _orthographic = hasCamera && camera.orthographic;
                _background = hasCamera ? camera.backgroundColor : default;
            }

            public bool Equals(PlacementSettings o) =>
                ReferenceEquals(_defaultMaterial, o._defaultMaterial) &&
                _fill.Equals(o._fill) && _padding.Equals(o._padding) && _depth.Equals(o._depth) &&
                _halfExtent.Equals(o._halfExtent) && _near.Equals(o._near) && _far.Equals(o._far) &&
                _autoFit == o._autoFit && _autoCenter == o._autoCenter &&
                _orthographic == o._orthographic && _background.Equals(o._background);

            public override bool Equals(object obj) => obj is PlacementSettings o && Equals(o);

            public override int GetHashCode() => HashCode.Combine(_fill, _padding, _depth, _halfExtent, _near, _far);
        }
    }
}
