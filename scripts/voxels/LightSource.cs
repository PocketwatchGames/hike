using System.Collections.Generic;
using Godot;

// A registered point light. Owned by WorldState.LightSources while active.
//
// The Footprint stores the full-amplitude deposit (the diffusion kernel at
// amplitude = 1). Flicker and pulse scale the deposit by Amplitude without
// recomputing the kernel — only the per-voxel array writes are needed.
//
// EffectiveBounds is 1 voxel larger than the kernel's non-zero extent. When
// a voxel changes within any source's EffectiveBounds, that source's kernel
// is recomputed (geometry near its edge may now let light through that was
// previously blocked, or vice versa).
public class LightSource
{
    public Vector3I Position;
    public Vector3 SubVoxelOffset = Vector3.Zero;
    public int Level;
    public Color Color = Colors.White;

    // Current brightness scalar [0, 1]. The deposited values in the world are
    // Footprint × Amplitude. Changing Amplitude is O(footprint) — just array
    // writes, no kernel recompute.
    public float Amplitude = 1f;

    // The kernel at full amplitude (Amplitude = 1).
    public readonly List<(Vector3I pos, ushort r, ushort g, ushort b)> Footprint = new();

    // AABB 1 voxel past the kernel's non-zero extent. Used by OnVoxelsChanged
    // to decide which sources need recomputation when geometry changes.
    public Vector3I BoundsMin;
    public Vector3I BoundsMax;
}
