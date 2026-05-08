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
    [Export] public TerrainData Terrain;

    // Detail-sprite group seeded by worldgen on voxels that carry this kit.
    // A forest kit points its DefaultDetail at detail_grass, a cave kit
    // at detail_pebbles, an underwater kit at null (no scatter). WorldGen
    // samples its detail noise field and stamps this group on matching
    // surface voxels.
    [Export] public DetailGroupData DefaultDetail;

    // Detail-sprite scatter tuning. Owned per-kit so kits within the same
    // zone can scatter independently (e.g. dense grass on the inland kit and
    // sparse seashells on the shore kit). The world-wide detail noise is
    // sampled with coords scaled by DetailNoiseFrequency, so each kit reads
    // a different noise pattern instead of sharing a thinned mask.
    //   DetailNoiseFrequency : 2D noise frequency for the scatter mask.
    //   DetailNoiseThreshold : noise values above this seed sprites.
    //   DetailStrengthMin    : minimum density (0..255) at the threshold.
    [Export] public float DetailNoiseFrequency = 0.06f;
    [Export] public float DetailNoiseThreshold = -0.1f;
    [Export] public int DetailStrengthMin = 80;

    // Tree-placement noise tuning, owned per-kit for the same reason as the
    // detail fields above: sharp tree-density transitions where one kit
    // borders another within a single zone (e.g. inland trees fading at the
    // shore kit). The forest noise is sampled with coords scaled by
    // ForestNoiseFrequency.
    //   ForestNoiseFrequency : 2D noise frequency for the forest mask.
    //   ForestThreshold      : noise values above this can place a tree.
    //   ForestDensity        : per-cell probability ramp from 0 at threshold
    //                          up to ForestDensity at noise = 1.
    [Export] public float ForestNoiseFrequency = 0.05f;
    [Export] public float ForestThreshold = 0.01f;
    [Export] public float ForestDensity = 0.5f;

    // Tree palette and per-chunk allotment for cells stamped with this kit.
    // The forest pass picks each tree's scene from the kit at the placement
    // cell, so two kits in one zone (e.g. shore vs inland) can carry
    // different palettes — palms on shore, pines inland — without a
    // kernel-blended mash-up at the seam. TreesPerChunkMin/Max drive the
    // baseline scatter that runs independent of forest noise; they're read
    // off the kit at the chunk center.
    [Export] public PackedScene[] TreeScenes = System.Array.Empty<PackedScene>();
    [Export] public PackedScene[] TallGrassScenes = System.Array.Empty<PackedScene>();
    [Export] public int TreesPerChunkMin = 0;
    [Export] public int TreesPerChunkMax = 4;
}
