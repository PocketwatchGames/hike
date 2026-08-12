using Godot;

// The WorldEditor's library of stampable prefabs — the scenes / loot the author
// paints with the Door, SpikeTrap, ClimbableTree, Torch, Campfire, Forge, Well, Fountain, Chest, Loot, and mob
// brushes. Lives on its own resource (not WorldGenData) because these are an
// editor-tool concern, not procedural-generation input: WorldGen.Generate never
// reads them. The Tree / TallGrass brushes are deliberately absent — they read
// the terrain kit stamped at the cursor so hand-placed foliage matches what the
// biome would scatter at that voxel.
[GlobalClass]
public partial class EditorBrushPalette : Resource
{
	// What the Terrain brush looks like WHILE AUTHORING — VoxelType.Terrain has
	// no fixed tile, so the editor needs some kit to resolve one from. It is not
	// scene content: a stamped scene's natural ground inherits the ground it
	// lands on (see SubsceneStamper), so a town square reads as mud in a swamp
	// and grass in a forest with nothing authored per scene. Pick whatever kit
	// makes the workspace legible; it must be referenced by some zone in the
	// loaded WorldGenData or it has no palette slot (see WorldGen.TryGetTerrainId).
	[ExportGroup("Terrain")]
	[Export] public TerrainKitData terrainBrushKit;

	// Source of the voxel brush-button icons. A PATH, not a resource reference,
	// on purpose — see EditorBrushIcons: a typed [Export] would drag the
	// manifest and every source PBR map into memory whenever main.tscn loads.
	[Export(PropertyHint.File, "*.tres")] public string atlasManifestPath;

	// Placeable props (trees, rocks, foliage). Authored here rather than read off
	// the worldgen kits: kit palettes are weighted lists meant to be rolled, are
	// limited to whatever the loaded WorldGenData references, and don't survive
	// into a .hike — none of which suits a hand-authoring catalog.
	[ExportGroup("Props")]
	[Export] public PropLibraryData propLibrary;

	// Surfaces the Roofs tool skins its generated geometry with. A roof has no
	// scene to stamp — the shape comes from the drag — so this is a list of
	// materials-plus-tuning rather than of prefabs.
	[ExportGroup("Roofs")]
	[Export] public RoofLibraryData roofLibrary;

	[ExportGroup("Interactives")]
	[Export] public PackedScene doorScene;
	[Export] public PackedScene spikeTrapScene;
	[Export] public PackedScene dartTrapScene;
	// Player-operated floor trapdoor; the perception-gated drop trap; and the
	// crumbling floor that looks like ground and breaks when stepped on.
	[Export] public PackedScene trapdoorScene;
	[Export] public PackedScene trapdoorTrapScene;
	[Export] public PackedScene crumblingFloorScene;
	// Lever that remote-throws a linked trapdoor. One "Lever: tag" + "Trapdoor:
	// tag" brush pair is generated per entry (see WorldEditor.AddLinkedTrapdoorBrushes),
	// sharing the tag so the lever throws that trapdoor.
	[Export] public PackedScene leverScene;
	[Export] public string[] linkTags = System.Array.Empty<string>();
	[Export] public PackedScene climbableTreeScene;
	[Export] public PackedScene torchScene;
	[Export] public PackedScene campfireScene;
	[Export] public PackedScene wellScene;
	// One scene per fountain variant — EFountainKind is authored on the scene's
	// Fountain node, so these are two separate brushes.
	[Export] public PackedScene healingFountainScene;
	[Export] public PackedScene manaFountainScene;

	[ExportGroup("Forge")]
	[Export] public PackedScene forgeScene;
	// Baked onto every editor-placed forge (worldgen instead reads the zone's
	// noise-modulated band). Until the entity tool grows a per-placement picker,
	// these are the whole authoring surface for a stamped forge.
	[Export(PropertyHint.Range, "0,4,1")] public int forgeLevel = 0;
	// None derives a stable slot from the placement position (ForgeOffer.SlotFor),
	// matching ForgeSpawnEntry; set it to stamp forges of one specific kind.
	[Export] public EUpgradeSlot forgeSlot = EUpgradeSlot.None;

	[ExportGroup("Chest")]
	[Export] public PackedScene chestScene;
	// Rolled into concrete ItemCounts when a chest is stamped (the same Resolve
	// path worldgen uses) and baked into the ChestSimState, so an editor-placed
	// chest drops a fixed loadout rather than re-rolling on open.
	[Export] public ItemCountRange[] chestLoot = System.Array.Empty<ItemCountRange>();

	[ExportGroup("Loot")]
	// The item (plus any permanent mods) a Loot brush stamp drops.
	[Export] public ItemDescriptor lootItem;

	[ExportGroup("Mobs")]
	[Export] public MobData goblinMob;
	[Export] public MobData kunKunMob;

	[ExportGroup("Markers")]
	// Pin drawn where a spawn marker stands. Editor-only art: worldgen consumes
	// markers at stamp time, so this scene never reaches a running game.
	[Export] public PackedScene markerScene;
	// One brush per pool name — placing a marker is picking the pool it belongs
	// to, so no tag has to be typed. Adding a pool means adding a string here;
	// a SubscenePlacement's variants fill these pools by the same name.
	[Export] public string[] markerTags = System.Array.Empty<string>();

	[ExportGroup("Path Hints")]
	// Pin drawn where a path hint stands — where a road is meant to touch this
	// scene. Editor-only art, like the spawn pin: worldgen turns hints into
	// points of interest at stamp time, so this scene never reaches a game.
	[Export] public PackedScene pathHintScene;
	// One brush per hint name. The name is what a RoadConnection addresses
	// ("<placement>.<tag>") AND what picks the tread an auto-linked spur gets
	// (WorldGenData.pathHintProfiles) — so "door" and "gate" are separate
	// brushes. Adding a kind of hint is a string here, not a code change.
	[Export] public string[] pathHintTags = System.Array.Empty<string>();

	// Forecasts offered by the editor's Weather dropdown, in menu order. The
	// selected one overrides the zone-blended weather so a scene can be
	// authored under a chosen sky; the first entry is what the editor opens
	// with. Each preset's inspector "Resource Name" is its menu label.
	[ExportGroup("View")]
	[Export] public WeatherData[] weatherPresets = System.Array.Empty<WeatherData>();
}
