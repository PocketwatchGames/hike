using Godot;

// One positional emitter "kind" — a stream + the rules for placing
// AudioStreamPlayer3D instances within a chunk based on the chunk's
// voxel/detail content. ChunkAmbienceSpawner walks each chunk's zone's
// palette on chunk load, deterministically picks instance positions
// from a chunk-coord-seeded RNG, and attaches the players as children
// of the chunk; the players go away when the chunk unloads.
//
// Examples (recommended setups):
//   * birds in trees   — spawnDetailGroupId = (tree group), instancesPerChunk = 1, TOD curve = day
//   * frogs by water   — spawnVoxelType = Water + requiresAdjacentSolid = true (shoreline cells), TOD = night
//   * crickets in grass — spawnDetailGroupId = (grass group), TOD = dusk-to-dawn
//   * dripping in caves — spawnVoxelType = Stone + requiresAirAbove = true, TOD = always
//
// Multiple emitters per zone are independent — each rolls its own
// instance positions from a per-emitter seed salt.
// [Tool] to match ZoneAmbienceData, which holds these.
[Tool]
[GlobalClass]
public partial class PositionalEmitterData : Resource
{
    [Export] public AudioStream stream;

    // Bus to play through. Defaults to World3D so the emitter goes
    // through the reverb send and picks up cave / building reverb
    // automatically when the player is in a tagged space.
    [Export] public string bus = "World3D";

    // Spawn-rule fields. The spawner walks the chunk's voxel grid and
    // a candidate cell qualifies if it matches all non-null/non-zero
    // criteria. Leave fields at their defaults to ignore that criterion.

    // If non-Air, only voxels of this type qualify. Use Water for
    // shoreline emitters, Stone for cave emitters.
    [Export] public int spawnVoxelType = Blocks.AirId;

    // If non-zero, only voxels with this exact DetailGroup id qualify
    // (matches painted scatter — grass, flowers, etc.).
    [Export] public byte spawnDetailGroupId = 0;

    // If true, the qualifying voxel must have an Air neighbor directly
    // above. Used for emitters that sit ON the surface — birds perched
    // on a tree, drips falling from a stone overhang.
    [Export] public bool requiresAirAbove = false;

    // If true, the qualifying voxel must have at least one solid
    // (non-Air, non-Water) horizontal neighbor — i.e. shoreline cells.
    [Export] public bool requiresAdjacentSolid = false;

    // Max instances spawned in any single chunk. The spawner uniformly
    // samples this many qualifying cells (with replacement from the
    // pool of candidates). Keep low — total concurrent emitters in
    // earshot is roughly chunks-in-radius × instancesPerChunk.
    [Export(PropertyHint.Range, "0,8,1")] public int instancesPerChunk = 1;

    // Time-of-day volume envelope, X = TimeOfDay01. Same shape /
    // semantics as AmbienceLayerData.timeOfDayVolume. Null = always
    // active (curve sample treated as 1.0).
    [Export] public Curve timeOfDayVolume;

    // Master per-emitter volume scale, applied after the TOD curve.
    [Export(PropertyHint.Range, "-40,12,0.5")] public float volumeDb = 0f;

    // Distance at which the player loses earshot of this emitter.
    // Drives AudioStreamPlayer3D.MaxDistance and the streaming-pause
    // boundary; default 24m matches "audible across a few chunks".
    [Export(PropertyHint.Range, "1,80,0.5")] public float maxDistance = 24f;

    // Salt mixed with the chunk coord for the deterministic spawn RNG.
    // Two emitters with the same fields but different seeds produce
    // non-overlapping placements in the same chunk.
    [Export] public int seed = 0;

    // Vertical offset (in voxels) added to the spawn voxel position.
    // Default 0.5 puts the emitter at the cell center; raise for "perched
    // on top" placement (birds in trees → 1.5 above the tree top voxel).
    [Export] public float yOffset = 0.5f;
}
