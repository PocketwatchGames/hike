using Godot;

// Base for the authored tuning of ONE terrain approach. `WorldGenData.terrain`
// holds a subclass, and that choice is what selects the algorithm that turns
// noise into a voxel grid — there is no mode enum and no per-approach fields on
// WorldGenData itself. Each subclass owns its own knobs, so tuning one approach
// cannot touch another, and an approach that is retired is deleted by removing
// two files rather than by picking its fields out of a shared resource.
//
// Only fields EVERY approach must answer for live here. The test is whether
// worldgen outside the approach reads it: the vertical-extent trio is consumed
// by WorldGen.FitVerticalExtent and maxGradeStep by the mesher's shape pass, so
// both sit on the base. Anything an approach alone reads belongs on the
// subclass, however tempting it is to share.
[GlobalClass]
public abstract partial class TerrainGenData : Resource
{
    // Air kept above the highest terrain column, in voxels. MUST be at least 1:
    // sunlight is seeded by a top-down column scan that breaks on the first
    // solid voxel, so a peak with no air above it lights nothing, and models /
    // particles sampling the light_map above the world top read the wrapped
    // underground band. The rest is flying room — a bird's-eye lift or a climb
    // above this line goes dark for the same reason.
    [Export(PropertyHint.Range, "1,64,1")] public int skyHeadroomVoxels = 12;

    // Solid rock kept below the lowest terrain column, in voxels. Sets how much
    // room the carving passes have to work in.
    [Export(PropertyHint.Range, "0,128,1")] public int undergroundDepthVoxels = 16;

    // Safety ceiling on terrain height, in voxels above sea level. Columns
    // above it are flattened before the extent is fitted, so a runaway relief
    // amplitude costs a visible mesa instead of quietly tripling world memory
    // (every chunk in the fitted box is allocated, ~62 KB each, air or not).
    // Sized as a guard rail — if it is shaping your terrain, lower the relief
    // amplitude rather than raising this.
    [Export(PropertyHint.Range, "16,512,1")] public int maxSurfaceHeightVoxels = 128;

    // Largest height difference between horizontally-adjacent columns still
    // treated as a GRADE (a staircase approximation of a slope, meshed smooth)
    // rather than a real discontinuity (meshed crisp). Raise only if an
    // approach authors grades steeper than this per column — they would
    // otherwise harden into visible stairs.
    [Export(PropertyHint.Range, "1,8,1")] public int maxGradeStep = 1;

    // Build the per-run generator. Implementations are one line: hand `this`,
    // the world data and the seed to the approach's ITerrainGenerator, which
    // lives in its own file under scripts/voxels/terrain/. Keeping the body
    // that thin is deliberate — no generation logic belongs on a Resource.
    public abstract ITerrainGenerator CreateGenerator(WorldGenData genData, int worldSeed);
}
