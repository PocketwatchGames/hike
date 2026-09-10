using Godot;

// ONE material a patch of ground can be MADE OF: the block it is stamped with,
// the detail sprites it wears up close, and how much moss grows on it. This is
// the unit the per-voxel TerrainId byte indexes.
//
// **A TerrainKitData is FOUR of these** — surface, shore, submerged, cave — and
// is what an author actually picks, because "this ground is forest" is one
// decision covering all four. Both producers work in these terms: the painter
// paints a terrain per column, WorldGen reads the four slots off ZoneGenData.
//
// **Nothing generator-only hangs off this.** What GROWS on the ground is
// `ZoneGenData.foliage`, because that is a property of the BIOME rather than of
// the material — the marsh terrain is the surface of both the swamp and the
// burning swamp, which want different woods. It used to be a `foliage` field
// here, which meant the PAINTER — which cannot read foliage at all — pulled the
// generator's whole tree/grass scene graph in behind every terrain it loaded.
//
// WorldGen builds its TerrainId palette from the terrains the zones name. Two
// zones naming the same terrain share a slot; two terrains naming the SAME block
// each keep their own, which is the feature — siblings in one zone (shore vs
// inland) scatter independently while painting identical ground.
//
// The channel outlives generation for exactly two reasons: ETerrainPurpose
// ("is this the zone's surface ground?") and these scatter tunings. Appearance
// is not among them — that moved to the block.
[GlobalClass]
public partial class TerrainData : Resource
{
    // What this material IS, for the passes that gate on it — the dirt overlay,
    // the surface scatter pick, road suppression, the moss cave/surface split,
    // and whether anything GROWS here (only a Surface terrain does). A property
    // of the MATERIAL, not of whichever zone happens to reference it: the same
    // rock is one zone's caves and everyone else's, and deriving this from a zone
    // list made it a side effect of zone placement. See ETerrainPurpose.
    [Export] public ETerrainPurpose purpose = ETerrainPurpose.None;

    // The block worldgen stamps for this terrain — what the ground actually
    // renders as. Required. Several legitimately share one (the four shore /
    // submerged terrains are all Sand); they stay distinct because their
    // scatter tunings below differ.
    [Export] public BlockData block;

    // Detail-sprite group seeded by worldgen on voxels that carry this terrain.
    // A forest terrain points its DefaultDetail at detail_grass, a cave terrain
    // at detail_pebbles, an underwater terrain at null (no scatter). WorldGen
    // samples its detail noise field and stamps this group on matching
    // surface voxels.
    [Export] public DetailGroupData defaultDetail;

    // How much of this material's exposed rock and ground wears the moss
    // overlay, 0..1. A property of the MATERIAL, so whoever paints the material
    // gets its moss with it — which is how the world-map painter answers
    // WorldFinish's per-column moss question: it paints ground per column, and
    // a column's surface terrain and cave terrain are exactly the two coverages the
    // pass asks for.
    //
    // WorldGen does NOT read this: it answers the same question from
    // ZoneGenData.mossSurfaceCoverage / mossCaveCoverage, because there moss
    // density is a property of the BIOME being generated rather than of the
    // ground that happened to be painted. Two producers, two answers, one pass
    // — the same split climb coverage already has (a zone fraction in worldgen,
    // an authored route flag in the painter).
    [Export(PropertyHint.Range, "0,1,0.01")] public float mossCoverage = 0f;

    // Detail-sprite scatter tuning. Owned per-terrain so terrains within the same
    // zone can scatter independently (e.g. dense grass on the inland terrain and
    // sparse seashells on the shore terrain). The world-wide detail noise is
    // sampled with coords scaled by DetailNoiseFrequency, so each terrain reads
    // a different noise pattern instead of sharing a thinned mask.
    //   DetailNoiseFrequency : 2D noise frequency for the scatter mask.
    //   DetailNoiseThreshold : noise values above this seed sprites.
    //   DetailStrengthMin    : minimum density (0..255) at the threshold.
    [Export] public float detailNoiseFrequency = 0.06f;
    [Export] public float detailNoiseThreshold = -0.1f;
    [Export] public int detailStrengthMin = 80;
}
