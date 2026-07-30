using Godot;

// The WorldEditor's library of stampable prefabs — the scenes / loot the author
// paints with the Door, SpikeTrap, ClimbableTree, Torch, Chest, Loot, and mob
// brushes. Lives on its own resource (not WorldGenData) because these are an
// editor-tool concern, not procedural-generation input: WorldGen.Generate never
// reads them. The Tree / TallGrass brushes are deliberately absent — they read
// the terrain kit stamped at the cursor so hand-placed foliage matches what the
// biome would scatter at that voxel.
[GlobalClass]
public partial class EditorBrushPalette : Resource
{
	// Terrain kit stamped as the per-voxel TerrainId behind the Terrain brush —
	// VoxelType.Terrain has no fixed tile, the shader resolves one from this.
	// A single default for now; the planned terrain picker turns this into the
	// author's current selection. The kit must be referenced by some zone in the
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
	[Export] public PackedScene climbableTreeScene;
	[Export] public PackedScene torchScene;

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

	// Forecasts offered by the editor's Weather dropdown, in menu order. The
	// selected one overrides the zone-blended weather so a scene can be
	// authored under a chosen sky; the first entry is what the editor opens
	// with. Each preset's inspector "Resource Name" is its menu label.
	[ExportGroup("View")]
	[Export] public WeatherData[] weatherPresets = System.Array.Empty<WeatherData>();
}
