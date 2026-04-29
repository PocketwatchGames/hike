using Godot;

// Per-region authored ambience set. Held by RegionData.ambience and
// blended at runtime alongside the visual region palette so a player
// crossing a biome border hears the new region fade in over the same
// few-chunk band that the sky is fading.
//
// Layer arrays are sets, not slots — a region can have any number of
// each kind, and overlapping layers add. A null entry in any array is
// safely ignored.
//
// Positional emitter palette is held here for step 6 (chunk-ambience
// spawner); referenced now so the data shape is stable for region
// authoring.
[GlobalClass]
public partial class RegionAmbienceData : Resource
{
    // Looping global layers. The "outdoor bed" of the region — base
    // wind, distant ocean, biome insect bed, rain (wetness-driven),
    // rain-on-leaves, etc. Each layer is independently driven by its
    // own AmbienceState field.
    [Export] public AmbienceLayerData[] globalLayers = System.Array.Empty<AmbienceLayerData>();

    // Per-emitter rules (which voxel/prop tags trigger spawn, density
    // per chunk, time-of-day mask) live on PositionalEmitterData;
    // ChunkAmbienceSpawner walks this palette when each chunk loads
    // and seeds deterministic instance positions per chunk coord.
    [Export] public PositionalEmitterData[] positionalEmitters = System.Array.Empty<PositionalEmitterData>();
}
