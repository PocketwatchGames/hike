using System.Collections.Generic;
using Godot;

// A registered point light. Owned by WorldState.LightSources while active.
//
// The Footprint stores the full-amplitude deposit (the flood footprint at
// amplitude = 1). Flicker and pulse scale the deposit by Amplitude without
// re-flooding — only the per-voxel array writes are needed.
//
// Bounds are 1 voxel larger than the footprint's non-zero extent. When a voxel
// changes within a source's bounds, its footprint is recomputed (geometry near
// its edge may now let light through that was previously blocked, or vice versa).
public class LightSource
{
    public Vector3I Position;
    public Color Color = Colors.White;

    // This light's falloff. Distance (reach) and Falloff (curve) together also
    // size its flood radius; Brightness is the open-space core intensity. Set by
    // the owning StationaryLight; defaults are a sane fallback. See
    // LightEngine.ResolveTuning.
    public float Distance = 10f;
    public float Falloff = 1.25f;
    public float Brightness = 0.9f;

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
