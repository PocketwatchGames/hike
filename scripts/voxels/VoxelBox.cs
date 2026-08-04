using Godot;

// An inclusive axis-aligned box of world voxel coordinates — the unit the
// REGIONAL world passes are scoped by, so an edit to non-voxel cover (a roof)
// re-stamps, relights and re-classifies its own footprint instead of the world.
public readonly struct VoxelBox : System.IEquatable<VoxelBox>
{
    public readonly Vector3I Min;
    public readonly Vector3I Max;

    public VoxelBox(Vector3I min, Vector3I max)
    {
        Min = min;
        Max = max;
    }

    // The identity for Union — any real box swallows it.
    public static VoxelBox Empty => new(new Vector3I(1, 1, 1), Vector3I.Zero);

    public bool IsEmpty => Max.X < Min.X || Max.Y < Min.Y || Max.Z < Min.Z;

    public VoxelBox Expand(int voxels)
    {
        if (IsEmpty)
        {
            return this;
        }
        var pad = new Vector3I(voxels, voxels, voxels);
        return new VoxelBox(Min - pad, Max + pad);
    }

    public VoxelBox Union(VoxelBox other)
    {
        if (IsEmpty) { return other; }
        if (other.IsEmpty) { return this; }
        return new VoxelBox(
            new Vector3I(Mathf.Min(Min.X, other.Min.X), Mathf.Min(Min.Y, other.Min.Y), Mathf.Min(Min.Z, other.Min.Z)),
            new Vector3I(Mathf.Max(Max.X, other.Max.X), Mathf.Max(Max.Y, other.Max.Y), Mathf.Max(Max.Z, other.Max.Z)));
    }

    public bool Equals(VoxelBox other)
    {
        return (IsEmpty && other.IsEmpty) || (Min == other.Min && Max == other.Max);
    }

    public static Vector3I ChunkOf(Vector3I voxel)
    {
        return new Vector3I(
            (int)System.Math.Floor((double)voxel.X / ChunkState.SIZE),
            (int)System.Math.Floor((double)voxel.Y / ChunkState.SIZE),
            (int)System.Math.Floor((double)voxel.Z / ChunkState.SIZE));
    }
}
