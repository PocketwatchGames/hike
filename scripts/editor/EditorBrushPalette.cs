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
	[ExportGroup("Interactives")]
	[Export] public PackedScene DoorScene;
	[Export] public PackedScene SpikeTrapScene;
	[Export] public PackedScene ClimbableTreeScene;
	[Export] public PackedScene TorchScene;

	[ExportGroup("Chest")]
	[Export] public PackedScene ChestScene;
	// Rolled into concrete ItemCounts when a chest is stamped (the same Resolve
	// path worldgen uses) and baked into the ChestSimState, so an editor-placed
	// chest drops a fixed loadout rather than re-rolling on open.
	[Export] public ItemCountRange[] ChestLoot = System.Array.Empty<ItemCountRange>();

	[ExportGroup("Loot")]
	// The item (plus any permanent mods) a Loot brush stamp drops.
	[Export] public ItemDescriptor LootItem;

	[ExportGroup("Mobs")]
	[Export] public MobData GoblinMob;
	[Export] public MobData KunKunMob;
}
