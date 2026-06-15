using Godot;

// Per-zone authored ambience set. Held by ZoneData.ambience and
// blended at runtime alongside the visual zone palette so a player
// crossing a biome border hears the new zone fade in over the same
// few-chunk band that the sky is fading.
//
// Layer arrays are sets, not slots — a zone can have any number of
// each kind, and overlapping layers add. A null entry in any array is
// safely ignored.
[GlobalClass]
public partial class ZoneAmbienceData : Resource
{
    // Looping global layers. The "outdoor bed" of the zone — base
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
