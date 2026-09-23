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
            public int Slot; public Mesh Mesh; public Material Material;
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
    }
}
