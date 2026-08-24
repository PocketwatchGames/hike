using Godot;

// Worldgen-only "kit" — the block a patch of ground is stamped with, plus the
// scatter/flora palette and tuning that WorldGen reads while stamping voxels
// and the running game never needs afterwards (tree placement, detail scatter
// mask, tree palette per cell, per-chunk baseline tree count).
//
// ZoneGenData references kits; WorldGen builds its TerrainId palette from them.
// Two zones referencing the same kit share a slot; two kits naming the SAME
// block each keep their own, which is the feature — sibling kits in one zone
// (shore vs inland) scatter independently while painting identical ground.
//
// The kit channel outlives generation for exactly two reasons: EKitPurpose
// ("is this the zone's surface ground?") and these scatter tunings. Appearance
// is not among them — that moved to the block.
[GlobalClass]
public partial class TerrainKitData : Resource
{
    // The block worldgen stamps for this kit — what the ground actually renders
    // as. Required. Several kits legitimately share one (the four shore /
    // submerged kits are all Sand); they stay distinct kits because their
    // scatter tunings below differ.
    [Export] public BlockData block;

    // Detail-sprite group seeded by worldgen on voxels that carry this kit.
    // A forest kit points its DefaultDetail at detail_grass, a cave kit
    // at detail_pebbles, an underwater kit at null (no scatter). WorldGen
    // samples its detail noise field and stamps this group on matching
    // surface voxels.
    [Export] public DetailGroupData defaultDetail;

    // How much of this material's exposed rock and ground wears the moss
    // overlay, 0..1. A property of the MATERIAL, so whoever paints the material
    // gets its moss with it — which is how the world-map painter answers
    // WorldFinish's per-column moss question: it paints ground per column, and
    // a column's surface kit and cave kit are exactly the two coverages the
    // pass asks for.
    //
    // WorldGen does NOT read this: it answers the same question from
    // ZoneGenData.mossSurfaceCoverage / mossCaveCoverage, because there moss
    // density is a property of the BIOME being generated rather than of the
    // ground that happened to be painted. Two producers, two answers, one pass
    // — the same split climb coverage already has (a zone fraction in worldgen,
    // an authored route flag in the painter).
    [Export(PropertyHint.Range, "0,1,0.01")] public float mossCoverage = 0f;

    // Detail-sprite scatter tuning. Owned per-kit so kits within the same
    // zone can scatter independently (e.g. dense grass on the inland kit and
    // sparse seashells on the shore kit). The world-wide detail noise is
    // sampled with coords scaled by DetailNoiseFrequency, so each kit reads
    // a different noise pattern instead of sharing a thinned mask.
    //   DetailNoiseFrequency : 2D noise frequency for the scatter mask.
    //   DetailNoiseThreshold : noise values above this seed sprites.
    //   DetailStrengthMin    : minimum density (0..255) at the threshold.
    [Export] public float detailNoiseFrequency = 0.06f;
    [Export] public float detailNoiseThreshold = -0.1f;
    [Export] public int detailStrengthMin = 80;

    // Tree-placement noise tuning, owned per-kit for the same reason as the
    // detail fields above: sharp tree-density transitions where one kit
    // borders another within a single zone (e.g. inland trees fading at the
    // shore kit). The forest noise is sampled with coords scaled by
    // ForestNoiseFrequency.
    //   ForestNoiseFrequency : 2D noise frequency for the forest mask.
    //   ForestThreshold      : noise values above this can place a tree.
    //   ForestDensity        : per-cell probability ramp from 0 at threshold
    //                          up to ForestDensity at noise = 1.
    [Export] public float forestNoiseFrequency = 0.05f;
    [Export] public float forestThreshold = 0.01f;
    [Export] public float forestDensity = 0.5f;

    // Tree palette and per-chunk allotment for cells stamped with this kit.
    // The forest pass picks each tree's scene from the kit at the placement
    // cell, so two kits in one zone (e.g. shore vs inland) can carry
    // different palettes — palms on shore, pines inland — without a
    // kernel-blended mash-up at the seam. TreesPerChunkMin/Max drive the
    // baseline scatter that runs independent of forest noise; they're read
    // off the kit at the chunk center.
    //
    // Each entry pairs a scene with a relative Frequency — WorldGen builds a
    // WeightedList from these and draws one per tree, so a commoner tree just
    // carries a higher Frequency instead of being listed multiple times. The
    // tall-grass palette works the same way.
    // The kit's ambient scatter, as a shared asset. Referencing one is what lets
    // a pine stand be defined ONCE and used by several kits and by the world-map
    // painter, instead of every kit carrying its own copy of the same list —
    // which is exactly how the two drifted apart.
    //
    // The inline fields below remain as the fallback for kits that have not been
    // migrated, and are what this resolves to when `forest` is null.
    [Export] public SpawnSetData forest;

    public WeightedScene[] Trees => forest != null && forest.treeScenes.Length > 0 ? forest.treeScenes : treeScenes;

    public WeightedScene[] Foliage => forest != null && forest.foliageScenes.Length > 0 ? forest.foliageScenes : tallGrassScenes;

    public float ForestFrequency => forest != null ? forest.forestNoiseFrequency : forestNoiseFrequency;

    public float ForestThreshold => forest != null ? forest.forestThreshold : forestThreshold;

    public float ForestDensity => forest != null ? forest.forestDensity : forestDensity;

    public int TreesPerChunkMin => forest != null ? forest.treesPerChunkMin : treesPerChunkMin;

    public int TreesPerChunkMax => forest != null ? forest.treesPerChunkMax : treesPerChunkMax;

    [Export] public WeightedScene[] treeScenes = System.Array.Empty<WeightedScene>();
    [Export] public WeightedScene[] tallGrassScenes = System.Array.Empty<WeightedScene>();
    [Export] public int treesPerChunkMin = 0;
    [Export] public int treesPerChunkMax = 4;
}
