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
// Kits (SurfaceKit, CaveKit, SubmergedKit, ShoreKit) bypass blending —
// they're typed refs read off the per-voxel kit stamp set during worldgen,
// so picking between palettes by fractional weight isn't meaningful. The
// global kit palette uploaded to the shader is built by deduplicating
// these refs across all zones — two zones that share the same gen kit
// cost one palette slot, not two.
//
// These slots reference TerrainKitData (the worldgen-side bundle), not
// TerrainData directly. The runtime visual entry is reached via
// `kit.Terrain`; tunings used only by worldgen (DefaultDetail, DetailNoise*,
// ForestNoise*/Threshold/Density, TreeScenes/TallGrassScenes/
// TreesPerChunkMin/Max) live on the kit. Sibling kits inside a single zone
// can scatter/forest at different densities and pull from different
// palettes — e.g. a shore strip with its own seashell scatter, zero trees,
// and palms while the inland kit keeps dense grass, pines, and a higher
// tree count. Density transitions are sharp at kit boundaries (each voxel
// reads its own kit slot) instead of kernel-blended.
//
// Mob / loot / chest / trap / campfire / berry-tree authoring lives in the
// SurfaceEntities / CaveEntities / ShoreEntities / WaterEntities lists.
// Each list is a SpawnListData asset (re-usable across zones), holding a
// polymorphic array of SpawnEntryData subclasses (MobSpawnEntry,
// LootSpawnEntry, ChestSpawnEntry, FireTrapSpawnEntry, BerryTreeSpawnEntry,
// CampfireSpawnEntry, TorchSpawnEntry). WorldGen scans the matching list
// per candidate cell, rolls each entry's SquareMetersPerSpawn area chance,
// and calls its Spawn().
[GlobalClass]
public partial class ZoneGenData : Resource
{
    [Export] public ZoneData Zone;

    // Gen kit stamped on surface terrain inside this zone (above sea level,
    // not inside caves). Drives the AUTO terrain shader's flat/wall tile
    // pick and the kit-controlled footstep + scatter behaviour. Null falls
    // back to the no-kit (palette index 0) appearance, which is a debug
    // state — author one for any zone you want to render correctly.
    [Export] public TerrainKitData SurfaceKit;

    // Gen kit stamped on solid voxels exposed to cave interior air. Lets a
    // forest's caves use limestone while a desert's caves use sandstone.
    // Authored independently of SurfaceKit because the cave shell often
    // wants a different ground category (Stone/Sand) and detail group.
    [Export] public TerrainKitData CaveKit;

    // Gen kit stamped on submerged seabed (solid voxels at or below water
    // level whose neighborhood touches water). Typically a shared
    // `kit_underwater` across zones, but can be specialized per zone.
    [Export] public TerrainKitData SubmergedKit;

    // Gen kit stamped on shore terrain — surface voxels that fall inside
    // the narrow band straddling water level (above water within
    // [ShoreElevationMin, ShoreElevationMax] meters and submerged within
    // [ShoreSubmergedElevationMin, ShoreSubmergedElevationMax] meters).
    [Export] public TerrainKitData ShoreKit;

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

    // Per-column random elevation range (in meters) used to pick where
    // ShoreKit is stamped. Shore band above water level is a random value
    // in [ShoreElevationMin, ShoreElevationMax]; shore band below water is
    // a random value in [ShoreSubmergedElevationMin,
    // ShoreSubmergedElevationMax]. Defaults yield a thin beach lip just
    // above sea level and a wider underwater shelf below it.
    [Export] public float ShoreElevationMin = 1f;
    [Export] public float ShoreElevationMax = 1.5f;
    [Export] public float ShoreSubmergedElevationMin = -5f;
    [Export] public float ShoreSubmergedElevationMax = -1f;

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

    // Per-zone authored entity spawn lists. WorldGen iterates the matching
    // list per candidate cell:
    //   SurfaceEntities — rolled per grass column (mobs, campfires, loot,
    //     berry trees, fire traps, etc.).
    //   CaveEntities    — rolled per cave-pocket air cell with solid floor
    //     and ceiling within reach (mobs, chests, loot, torches).
    //   ShoreEntities   — reserved for shore-band columns. Currently unused;
    //     wire up when shore-specific authoring lands.
    //   WaterEntities   — reserved for submerged cells. Currently unused;
    //     wire up when underwater authoring lands.
    // Lists are SpawnListData assets so multiple zones can share the same
    // file (e.g. all biomes pointing at one shared cave_entities.tres).
    [Export] public SpawnListData SurfaceEntities;
    [Export] public SpawnListData CaveEntities;
    [Export] public SpawnListData ShoreEntities;
    [Export] public SpawnListData WaterEntities;
}
