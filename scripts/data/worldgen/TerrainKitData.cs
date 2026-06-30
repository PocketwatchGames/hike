using Godot;

// Worldgen-only "kit" — a bundle of a TerrainData (visual ground) plus the
// scatter/flora palette and tuning fields that WorldGen reads while stamping
// voxels but that the running game never needs after the world is built
// (tree placement, detail scatter mask, tree palette per cell, per-chunk
// baseline tree count).
//
// One kit wraps one runtime TerrainData via Terrain. ZoneGenData references
// kits; WorldGen builds its terrain-id palette from those kits and derives a
// parallel runtime palette by reading Terrain on each. Two zones referencing
// the same TerrainKitData share both gen and runtime slots; two kits whose
// Terrain points at the same TerrainData each get their own slot, which is
// the feature — sibling kits in one zone (e.g. shore vs inland) can scatter
// independently while sharing visual ground.
//
// Splitting these fields off lets a future streaming world skip loading the
// kit palette entirely once a `.hike` file has been authored — TreeScenes in
// particular drag in PackedScene trees the runtime never needs.
[GlobalClass]
public partial class TerrainKitData : Resource
{
    // The runtime visual / footstep / tile entry this kit wraps. Required —
    // WorldGen.BuildKitPalette derives the runtime palette by reading Terrain
    // per slot, so a null Terrain produces a null runtime entry and any
    // voxel stamped with that slot will fall back to the no-terrain
    // appearance.
    [Export] public TerrainData terrain;

    // Detail-sprite group seeded by worldgen on voxels that carry this kit.
    // A forest kit points its DefaultDetail at detail_grass, a cave kit
    // at detail_pebbles, an underwater kit at null (no scatter). WorldGen
    // samples its detail noise field and stamps this group on matching
    // surface voxels.
    [Export] public DetailGroupData defaultDetail;

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
    [Export] public WeightedScene[] treeScenes = System.Array.Empty<WeightedScene>();
    [Export] public WeightedScene[] tallGrassScenes = System.Array.Empty<WeightedScene>();
    [Export] public int treesPerChunkMin = 0;
    [Export] public int treesPerChunkMax = 4;
}
