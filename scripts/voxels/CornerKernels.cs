using Godot;

// Pre-computed diffusion kernels for the 8 corners of a voxel, used by
// MovingLight for smooth sub-voxel interpolation. Each corner kernel is the
// steady-state diffusion result from a single-point seed at that corner of
// the source voxel, run against the surrounding geometry.
//
// At runtime, MovingLight blends the 8 kernels by trilinear weights derived
// from the carrier's sub-voxel position. Because diffusion is linear, this
// weighted sum equals the result of one diffusion seeded at the exact sub-
// voxel position — but without rerunning the diffusion per frame.
//
// On voxel crossing, all 8 kernels are recomputed (~8 diffusions). Between
// crossings, only the blend weights change — O(volume) float reads + writes,
// no diffusion. Future optimization: adjacent voxels share 4 of 8 corners,
// so only 4 need recomputing per crossing.
public class CornerKernels
{
    public int Reach;
    public int Dim;
    public int Total;
    public Vector3I Origin;
    public bool[] Open;

    // 8 corner kernels. Index layout: [corner 0..7][voxelIdx 0..Total-1].
    // Corner ordering: c = cx | (cy << 1) | (cz << 2), where cx/cy/cz ∈ {0,1}
    // offset from the source voxel Position.
    public float[][] R;
    public float[][] G;
    public float[][] B;

    // Per-corner seed index into the buffer (where the point source lives).
    public int[] SeedIdx;
    // Per-corner flag: true if the seed voxel was open (light can emit there).
    public bool[] SeedOpen;

    // Sparse list of buffer indices where ANY corner has a non-zero value.
    // BlendAndDeposit iterates this instead of the full dim³ volume, skipping
    // the ~90% of voxels that are zero across all corners.
    public int[] NonZeroIndices;
    public int NonZeroCount;
}
