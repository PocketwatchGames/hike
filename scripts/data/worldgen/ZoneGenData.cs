using Godot;

// Per-zone world generation parameters. WorldGenData.Zones[] holds one of
// these per zone; each entry's index becomes the ChunkState.ZoneIndex
// stamped on every chunk it owns. WorldGen blends each per-position scalar
// across a chunk-kernel so transitions between adjacent zones are smooth
// rather than snapping at chunk borders.
//
// TERRAIN TUNING IS NOT HERE — it lives on the `terrain` sub-resource, one
// subclass per approach. Adding a terrain field to this class is what let two
// approaches' knobs end up interleaved with nothing marking which was which.
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
    [Export] public ZoneData zone;

    // Gen kit stamped on surface terrain inside this zone (above sea level,
    // not inside caves). Drives the AUTO terrain shader's flat/wall tile
    // pick and the kit-controlled footstep + scatter behaviour. Null falls
    // back to the no-kit (palette index 0) appearance, which is a debug
    // state — author one for any zone you want to render correctly.
    [Export] public TerrainKitData surfaceKit;

    // Gen kit stamped on solid voxels exposed to cave interior air. Lets a
    // forest's caves use limestone while a desert's caves use sandstone.
    // Authored independently of SurfaceKit because the cave shell often
    // wants a different ground category (Stone/Sand) and detail group.
    [Export] public TerrainKitData caveKit;

    // Gen kit stamped on submerged seabed (solid voxels at or below water
    // level whose neighborhood touches water). Typically a shared
    // `kit_underwater` across zones, but can be specialized per zone.
    [Export] public TerrainKitData submergedKit;

    // Gen kit stamped on shore terrain — surface voxels that fall inside
    // the narrow band straddling water level (above water within
    // [ShoreElevationMin, ShoreElevationMax] meters and submerged within
    // [ShoreSubmergedElevationMin, ShoreSubmergedElevationMax] meters).
    [Export] public TerrainKitData shoreKit;

    // This zone's terrain tuning, as a subclass matching the approach the world
    // runs (OrganicZoneTerrainData, PlateauZoneTerrainData, ...). Holding it in
    // one polymorphic slot is what keeps a zone authored for one approach free
    // of another's knobs — they used to sit side by side here with nothing
    // marking which was which. Null falls back to the base defaults.
    [Export] public ZoneTerrainData terrain;

    // Per-column random elevation range (in meters) used to pick where
    // ShoreKit is stamped. Shore band above water level is a random value
    // in [ShoreElevationMin, ShoreElevationMax]; shore band below water is
    // a random value in [ShoreSubmergedElevationMin,
    // ShoreSubmergedElevationMax]. Defaults yield a thin beach lip just
    // above sea level and a wider underwater shelf below it.
    [Export] public float shoreElevationMin = 1f;
    [Export] public float shoreElevationMax = 1.5f;
    [Export] public float shoreSubmergedElevationMin = -5f;
    [Export] public float shoreSubmergedElevationMax = -1f;

    [Export] public float grassThreshold = 0.3f;

    // Monster difficulty band for this zone. WorldGen samples a low-frequency
    // world-space noise field per spawn and lerps between these two across it, so
    // a zone ramps from MobLevelMin at one end of its footprint to MobLevelMax at
    // the other rather than sitting at one flat difficulty. Blended across zone
    // borders like the other per-position scalars (see WorldGen.ComputeMobLevel).
    // Mobs add their species base level on top. Keep the span small — each level
    // scales a monster's health/armor/damage by SimData.levelScalePerLevel
    // (~1.5x/level).
    [Export(PropertyHint.Range, "0,4,1")] public int mobLevelMin = 0;
    [Export(PropertyHint.Range, "0,4,1")] public int mobLevelMax = 3;

    // Difficulty band for monsters spawned UNDERGROUND in this zone — a cave,
    // tunnel, or anything with a ceiling overhead. Sampled from the same noise
    // field as the surface band, so a cave inherits the difficulty gradient of
    // the ground above it, just shifted: author these ~1 level above
    // MobLevelMin/Max so descending reads as an escalation. Authored per zone
    // rather than derived as a flat bonus because the shift a zone wants isn't
    // uniform — a top-band zone has no headroom under MobLevelCap, and a
    // starting zone may want its caves to jump further than +1.
    [Export(PropertyHint.Range, "0,4,1")] public int undergroundMobLevelMin = 1;
    [Export(PropertyHint.Range, "0,4,1")] public int undergroundMobLevelMax = 4;

    // How many smithing forges WorldGen scatters into this zone (each on its
    // own rejection-sampled flat column within the zone's bounds). Drives
    // PlaceZoneForges directly — a large zone (e.g. an EverywhereBounds base
    // zone spanning the whole map) needs several to not feel empty, while the
    // spawn/village zone typically sets this to 0. The forge scene itself is
    // authored once on WorldGenData.forge; this only controls the count.
    [Export(PropertyHint.Range, "0,10,1")] public int forgeCount = 1;

    // Forge power band for this zone, sampled the same way but from an
    // INDEPENDENT noise field (see WorldGen.ComputeForgeLevel), so a zone's
    // forges and monsters vary in difficulty separately. Drives the forge's
    // granted-upgrade strength and star pips (0-4).
    [Export(PropertyHint.Range, "0,4,1")] public int forgeLevelMin = 0;
    [Export(PropertyHint.Range, "0,4,1")] public int forgeLevelMax = 4;

    // Per-zone authored entity spawn lists. WorldGen iterates the matching
    // list per candidate cell:
    //   SurfaceEntities — rolled per grass column (mobs, campfires, loot,
    //     berry trees, fire traps, etc.).
    //   CaveEntities      — rolled per cave-pocket AIR cell with solid floor
    //     and ceiling within reach (mobs, chests, loot, torches).
    //   CaveWaterEntities — rolled per flooded cave pocket: the top voxel of a
    //     roofed underwater body. Separate from WaterEntities because caves
    //     below sea level flood by construction, and what belongs in a drowned
    //     tunnel is not what belongs in the open sea.
    //   ShoreEntities     — reserved for shore-band columns. Currently unused;
    //     wire up when shore-specific authoring lands.
    //   WaterEntities     — rolled per open water-surface column (lakes, sea).
    // Lists are SpawnListData assets so multiple zones can share the same
    // file (e.g. all biomes pointing at one shared cave_entities.tres).
    [Export] public SpawnListData surfaceEntities;
    [Export] public SpawnListData caveEntities;
    [Export] public SpawnListData caveWaterEntities;
    [Export] public SpawnListData shoreEntities;
    [Export] public SpawnListData waterEntities;

    // Zone-unique chest loot, in two distribution modes:
    //
    // perChestLoot — rolled INDEPENDENTLY at every chest in this zone (cave
    // chests and camp-group chests alike), appended to each chest's own
    // lootItems. Use for common filler that should vary chest-to-chest (a
    // health potion, some coins). Threaded to ChestSpawnEntry at spawn time via
    // SpawnContext, so each chest gets its own roll.
    //
    // distributedLoot — a set TOTAL quantity spread ACROSS the zone's chests as
    // a whole: each entry's rolled count is the number of copies placed into
    // randomly chosen chests (one per chest, round-robin, wrapping only if the
    // total exceeds the chest count), NOT a per-chest amount. Use for
    // important / quest items that should appear a fixed number of times in the
    // zone rather than in every chest (a region recipe cookbook). Resolved in a
    // post-generation pass (WorldGen.DistributeZoneLoot) once all chests exist;
    // a zone with no chests simply doesn't place its distributed loot.
    [Export] public ItemCountRange[] perChestLoot = System.Array.Empty<ItemCountRange>();
    [Export] public ItemCountRange[] distributedLoot = System.Array.Empty<ItemCountRange>();

    // World-unique name for this zone's one buried treasure. A treasure map
    // (RevealTreasureMapEffect.treasureName) points at the spot by this name, so
    // the map->treasure link is fixed at authoring/worldgen, not resolved
    // dynamically. Empty = this zone has no treasure. Set treasureSpot too.
    [Export] public string treasureName = "";

    // The buried treasure placed once inside this zone by WorldGen.PlaceZoneTreasures
    // (a BuriedSpotSpawnEntry supplying the shared buried_spot scene + the payload
    // BuriedSpotData — the song scroll or crowns). Its location is stamped into
    // WorldState.TreasureSpots under treasureName. Null = no treasure this zone.
    [Export] public BuriedSpotSpawnEntry treasureSpot;

    // One-off landmark cluster placed ONCE per zone at the zone's anchor
    // (vs the SurfaceEntities density scan), e.g. a "home" campfire. The anchor
    // comes from this zone's ZoneBounds (see PlacedZone): box/circle bounds
    // anchor at their center — used by a start zone placed around the spawn
    // column for the near-spawn cluster (villager, dog, lit campfire) — while
    // quadrant/everywhere bounds roll a flat-dry column within their footprint.
    // The group's ScatterRadius spreads the members around that anchor. Null =
    // no per-zone cluster.
    [Export] public SpawnGroupData fixtures;

    // Names of points of interest located in this zone placement. WorldGen
    // resolves each to a random flat column inside this zone's bounds and
    // registers it in WorldState.PointsOfInterest, where road connections
    // (WorldGenData.Roads) and POI-anchored spawns
    // (WorldGenData.PointsOfInterestPlacements) reference it by name. Lives here
    // rather than on ZoneData because POIs are per-placement: a world can reuse
    // one ZoneData theme across several ZoneGenData placements (e.g. the swamp
    // world's village / mud / highlands all share swamp.tres) yet wants a
    // distinct POI per placement. Names are world-unique — the first placement
    // listing a given name resolves it.
    [Export] public string[] pointsOfInterest = System.Array.Empty<string>();
}
