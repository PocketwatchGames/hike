using Godot;

// Per-zone world generation parameters. WorldGenData.Zones[] holds one of
// these per zone; each entry's index becomes the ChunkState.ZoneIndex
// stamped on every chunk it owns. WorldGen blends each per-position scalar
// (BaseElevation, ElevationRange, thresholds, densities) across a chunk-kernel
// so transitions between adjacent zones are smooth rather than snapping at
// chunk borders.
//
// `Zone` is the authored theme + weather profile for this zone. WorldGen
// copies it into WorldState.Zones[i].Data, where it drives sky/water tinting
// and weather blending at runtime.
//
// Per-chunk and global parameters (TreeScenes, TallGrassScenes,
// TreesPerChunkMin/Max, Kits, DetailGroups) bypass blending — picking
// between palettes by fractional weight isn't meaningful. Those are read off
// the chunk's authoritative ZoneIndex (or, for the truly-global Kit /
// DetailGroup palette uploaded to the shader, off the first zone — see
// Main.StartGame).
[GlobalClass]
public partial class ZoneGenData : Resource
{
    [Export] public ZoneData Zone;

    // Environment kits used for AUTO terrain. Index in this array == KitId
    // stored per-voxel in ChunkState. Index 0 is the fallback / default kit.
    // The shader uploads a single global kit palette (see ChunkMesh.SetKits)
    // sourced from the first zone — cross-zone kit divergence isn't
    // supported yet because per-voxel KitId is a single index into one
    // palette. Authoring different Kits arrays per zone is a no-op until
    // that's resolved, so keep them identical for now.
    [Export] public EnvironmentKitData[] Kits = System.Array.Empty<EnvironmentKitData>();

    // Detail-sprite scatter palette. Same single-global-palette rule as Kits
    // — first zone wins for the live registry.
    [Export] public DetailGroupData[] DetailGroups = System.Array.Empty<DetailGroupData>();

    // Authored center elevation for the zone, in PlateauStep units. The
    // value is treated as the elevation at the zone's center; WorldGen
    // kernel-blends it across (wx, wz) so adjacent zones transition
    // smoothly. +1 reads as one plateau step above sea level, -1 as one
    // below. Inland zones sit at +1 by convention; wetlands at -1; sea
    // shelves at 0.
    [Export] public float Elevation = 0;

    // Half-amplitude of plateau variation, in PlateauStep units. Per-column
    // plateau height is approximately
    //   (Elevation + ElevationRange * terrainNoise) × PlateauStep
    // quantized to a multiple of PlateauStep before the coastal falloff.
    // Mountain zones push this up for dramatic peaks; flat zones keep
    // it lower.
    [Export] public float ElevationRange = 2;

    // Path height-smoothing parameters (currently unused by WorldGen — kept
    // here as authored knobs reserved for the path/ramp authoring pass).
    [Export] public float PathThreshold = 0.1f;
    [Export] public float PathBlendBand = 0.05f;

    [Export] public float TunnelThreshold = 0.1f;

    // Cave 3D-noise frequency. The world-wide cave noise object uses the
    // first zone's value (one shared FastNoiseLite per worldgen run);
    // CaveThreshold can still vary per-zone to make some zones cavernous
    // and others nearly solid.
    [Export] public float CaveNoiseFrequency = 0.04f;
    [Export] public float CaveThreshold = 0.25f;

    [Export] public float GrassThreshold = 0.3f;

    [Export] public int TreesPerChunkMin = 0;
    [Export] public int TreesPerChunkMax = 4;

    // Forest 2D-noise frequency. Like CaveNoiseFrequency, the global forest
    // noise object uses the first zone's value.
    [Export] public float ForestNoiseFrequency = 0.05f;
    [Export] public float ForestThreshold = 0.01f;
    [Export] public float ForestDensity = 0.5f;

    [Export] public PackedScene[] TreeScenes = System.Array.Empty<PackedScene>();
    [Export] public PackedScene[] TallGrassScenes = System.Array.Empty<PackedScene>();

    // Detail-sprite scatter tuning. The world-wide detail noise object uses
    // the first zone's frequency (single shared FastNoiseLite); the
    // threshold and minimum-strength values blend per-column so a zone's
    // grass density can fall off naturally at its borders.
    //   DetailNoiseFrequency : 2D noise frequency for the scatter mask.
    //   DetailNoiseThreshold : noise values above this seed sprites.
    //   DetailStrengthMin    : minimum density (0..255) at the threshold.
    [Export] public float DetailNoiseFrequency = 0.06f;
    [Export] public float DetailNoiseThreshold = -0.1f;
    [Export] public int DetailStrengthMin = 80;

    // Per-zone mob / loot / chest authoring. Each prop pass picks a
    // kernel-weighted zone per (wx, wz), then rolls the chosen zone's
    // chance and (if it hits) spawns the chosen zone's scene. Set a
    // chance to 0 or null out a scene to disable that prop type for the
    // zone (e.g. desert with no goblins, marsh with no chests).
    [Export] public PackedScene GoblinScene;
    [Export] public MobData GoblinData;
    // Per-(grass column) spawn chance for goblins on the surface. These spawn
    // marked SpawnAtNight, so their nodes only appear when the chunk is loaded
    // after dark.
    [Export] public float GoblinSpawnOutsideNighttime = 0.005f;
    // Per-(cave-pocket cell) spawn chance for goblins underground. Always
    // spawns regardless of time of day.
    [Export] public float GoblinSpawnUnderground = 0.005f;

    // Per-(grass column) spawn chance for campfires on the surface. Authored
    // at 1/5 the goblin nighttime rate by convention. Spawned campfires are
    // marked AutoLightAtNight so they ignite when their chunk activates after
    // dark and stay dark in daylight.
    [Export] public float CampfireSpawnOutside = 0.001f;

    // Optional spawn group scattered around each campfire when it spawns.
    // Use a MobSpawnEntry with SpawnAtNight=true plus a ChestSpawnEntry to
    // populate a night-only encampment around the fire.
    [Export] public SpawnGroupData CampfireSpawnGroup;

    [Export] public PackedScene KunKunScene;
    [Export] public MobData KunKunData;
    [Export] public float KunKunChance = 0.005f;

    [Export] public PackedScene LootScene;
    [Export] public float LootChance = 0.005f;

    [Export] public PackedScene ChestScene;
    [Export] public float ChestChance = 0.002f;
    [Export] public int ChestLootCountMin = 3;
    [Export] public int ChestLootCountMax = 6;

    // Per-(grass column) spawn chance for fire-column traps on the surface.
    // Authored only on the swamp region by default — the "fire swamp" hazard
    // pattern from The Princess Bride. Each spawned trap gets a random phase
    // offset so neighbouring columns don't fire in lockstep.
    [Export] public PackedScene FireTrapScene;
    [Export] public float FireTrapChance = 0f;

    // Per-(grass column) spawn chance for berry trees on the surface. Each
    // tree drops [BerryCountMin..BerryCountMax] autoloot berries when picked
    // (or when the hurtbox takes a sword hit). Leave BerryTreeScene null to
    // disable for the region.
    [Export] public PackedScene BerryTreeScene;
    [Export] public float BerryTreeChance = 0.01f;
    [Export] public int BerryTreeCountMin = 3;
    [Export] public int BerryTreeCountMax = 6;
}
