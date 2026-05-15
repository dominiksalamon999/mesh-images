using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using static MeshImages.MeshImageAtlasUtility;

namespace MeshImages
{
    /// <summary>
    /// Slot data backing one cell of the atlas. <see cref="Slot.Go"/> hosts the
    /// preview mesh that <see cref="MeshImageAtlas"/>'s camera renders into the
    /// matching atlas region.
    /// </summary>
    internal struct Slot
    {
        public GameObject Go;
        public MeshFilter Filter;
        public MeshRenderer Renderer;
    }

    /// <summary>
    /// Layout configuration threaded into pool calls each invocation so the pool
    /// stays decoupled from <see cref="MeshImageAtlas"/>. Pass by <c>in</c> to
    /// avoid the copy.
    /// </summary>
    internal readonly struct SlotLayoutConfig
    {
        public readonly int GridSize;
        public readonly float UvPaddingFraction;
        public readonly float PreviewDepth;
        public readonly float ViewHalfExtent;

        public SlotLayoutConfig(int gridSize, float uvPaddingFraction, float previewDepth, float viewHalfExtent)
        {
            GridSize = gridSize;
            UvPaddingFraction = uvPaddingFraction;
            PreviewDepth = previewDepth;
            ViewHalfExtent = viewHalfExtent;
        }

        public int MaxSlots => GridSize * GridSize;
        public float CellSize => 2f * ViewHalfExtent / GridSize;
    }

    /// <summary>
    /// Fixed-capacity pool of grid cells holding preview meshes for the atlas.
    /// Owns the <see cref="Slot"/> array, the free list, and the deferred-deactivate
    /// queue. Knows nothing about MeshImages, render textures, or play/edit mode —
    /// callers thread the layout config and a deactivate-defer callback in.
    /// </summary>
    internal sealed class MeshImageSlotPool
    {
        private const string SlotName = "MeshImage_Slot";

        private readonly Queue<int> _freeSlots = new();
        private readonly Queue<int> _pendingDeactivate = new();
        private Slot[] _slots;
        private int _nextSlot, _builtGridSize = -1;

        /// <summary>
        /// Fires when <see cref="ReleaseSlot"/> enqueues a slot for deferred
        /// deactivation. The atlas uses this to wire up an editor-update tick
        /// during edit mode (LateUpdate handles play mode itself).
        /// </summary>
        public event Action DeactivateQueued;

        public int Capacity => _slots?.Length ?? 0;
        public Slot this[int slot] => _slots[slot];

        /// <summary>True if the pool was never built or is built for a different grid size.</summary>
        public bool NeedsRebuild(int gridSize) => _slots == null || _builtGridSize != gridSize;

        /// <summary>
        /// Tear down and recreate the slot GameObjects under <paramref name="parent"/>.
        /// All bookkeeping (free list, deactivate queue, next-slot cursor) is reset;
        /// callers are responsible for re-placing any registrations they were tracking.
        /// </summary>
        public void Build(Transform parent, int gridSize, int previewLayer)
        {
            Destroy();
            int capacity = gridSize * gridSize;
            _slots = new Slot[capacity];

            for (int i = 0; i < capacity; i++)
            {
                var go = new GameObject($"{SlotName}_{i}", typeof(MeshFilter), typeof(MeshRenderer))
                { hideFlags = HideFlags.HideAndDontSave, layer = previewLayer };
                go.transform.SetParent(parent, false);
                go.SetActive(false);

                var mr = go.GetComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

                _slots[i] = new Slot { Go = go, Filter = go.GetComponent<MeshFilter>(), Renderer = mr };
            }

            _builtGridSize = gridSize;
            _freeSlots.Clear();
            _pendingDeactivate.Clear();
            _nextSlot = 0;
        }

        /// <summary>Destroy all slot GameObjects and reset bookkeeping. Safe to call multiple times.</summary>
        public void Destroy()
        {
            if (_slots != null)
                for (int i = 0; i < _slots.Length; i++)
                    if (_slots[i].Go != null) DestroySafe(_slots[i].Go);
            _slots = null;
            _builtGridSize = -1;
            _freeSlots.Clear();
            _pendingDeactivate.Clear();
            _nextSlot = 0;
        }

        /// <summary>
        /// Acquire the next free slot. Returns false (with slot = -1) when capacity is reached.
        /// </summary>
        public bool TryAcquireSlot(out int slot)
        {
            if (_freeSlots.Count > 0) { slot = _freeSlots.Dequeue(); return true; }
            if (_nextSlot >= Capacity) { slot = -1; return false; }
            slot = _nextSlot++;
            return true;
        }

        /// <summary>
        /// Clear the slot's mesh/material, defer the SetActive(false) (forbidden in
        /// some Unity callbacks), and return the slot to the free pool. If a deactivate
        /// was actually queued, raises <see cref="DeactivateQueued"/> exactly once per
        /// release so the caller can schedule an editor-update tick if needed.
        /// </summary>
        public void ReleaseSlot(int slot)
        {
            if (_slots == null || slot < 0 || slot >= _slots.Length) return;
            var s = _slots[slot];
            if (s.Filter != null) s.Filter.sharedMesh = null;
            if (s.Renderer != null) s.Renderer.sharedMaterial = null;

            // SetActive(false) fires OnBecameInvisible, forbidden during
            // OnValidate/Awake/CheckConsistency. Defer it. If the slot is
            // re-acquired before the drain runs, TryAdd's SetActive(true)
            // wins and DrainPendingDeactivates skips it.
            bool queued = false;
            if (s.Go != null)
            {
                _pendingDeactivate.Enqueue(slot);
                queued = true;
            }
            _freeSlots.Enqueue(slot);

            if (queued) DeactivateQueued?.Invoke();
        }

        /// <summary>
        /// Apply the deferred SetActive(false) to slots queued by <see cref="ReleaseSlot"/>.
        /// Skips slots that have been re-acquired since they were queued.
        /// </summary>
        public void DrainPendingDeactivates()
        {
            if (_slots == null) { _pendingDeactivate.Clear(); return; }

            while (_pendingDeactivate.Count > 0)
            {
                int slot = _pendingDeactivate.Dequeue();
                if (slot < 0 || slot >= _slots.Length) continue;
                var s = _slots[slot];
                if (s.Go == null) continue;
                // If the slot was re-acquired since deferral, leave it active.
                if (s.Filter != null && s.Filter.sharedMesh != null) continue;
                s.Go.SetActive(false);
            }
        }

        /// <summary>Restamp every slot GameObject's layer. Called when the atlas detects a culling-mask change.</summary>
        public void SetPreviewLayer(int newLayer)
        {
            if (_slots == null) return;
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].Go != null) _slots[i].Go.layer = newLayer;
        }

        /// <summary>
        /// UV rect inside the atlas texture for a given slot, shrunk by the configured padding.
        /// Row 0 is at the top of the texture (UV y = 1).
        /// </summary>
        public static Rect GetSlotUvRect(int slot, in SlotLayoutConfig cfg)
        {
            float v = 1f / cfg.GridSize, pad = v * cfg.UvPaddingFraction;
            int col = slot % cfg.GridSize, row = slot / cfg.GridSize;
            return new Rect(col * v + pad, (1f - v) - row * v + pad, v - 2f * pad, v - 2f * pad);
        }

        /// <summary>
        /// Local-space position of a slot's center in front of the atlas camera,
        /// offset by <paramref name="offset"/>.
        /// </summary>
        public static Vector3 GetSlotLocalPosition(int slot, Vector3 offset, in SlotLayoutConfig cfg)
        {
            int col = slot % cfg.GridSize, row = slot / cfg.GridSize;
            float cell = cfg.CellSize, half = cfg.ViewHalfExtent;
            return new Vector3(-half + (col + 0.5f) * cell,
                               +half - (row + 0.5f) * cell,
                               cfg.PreviewDepth) + offset;
        }
    }
}