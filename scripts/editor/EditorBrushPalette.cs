using Godot;

// What the WorldEditor's tools need that is NOT a placeable thing: the atlas its
// voxel-brush icons come from, the roof surfaces the Roofs tool skins geometry
// with, and the skies a scene can be authored under.
//
// The placeable things used to be here too — a PackedScene per interactive, a
// MobData per mob, plus the chest loot, forge tier and item every stamp of one
// got. That was a second registration of what the world-map painter already
// discovered from `spawn_entries/`, and it drifted exactly as a duplicate does:
// this file carried UIDs for the forge and both fountains that matched nothing
// in the repo, and every editor-placed chest in every scene held the same one
// potion because the loadout was a field here rather than a per-placement value.
// The editor now reads AuthoringPaletteSource like the painter does.
//
// Roofs stay because a roof has no scene to place — the shape comes from the
// drag — so a style is a material plus tuning, not a prefab.
[GlobalClass]
public partial class EditorBrushPalette : Resource
{
	// The baked terrain atlas the voxel brush-button icons are cut from, so an
	// icon is the tile the game actually draws.
	[Export] public TextureLayered tileAtlas;

	// Surfaces the Roofs tool skins its generated geometry with. A roof has no
	// scene to stamp — the shape comes from the drag — so this is a list of
	// materials-plus-tuning rather than of prefabs.
	[ExportGroup("Roofs")]
	[Export] public RoofLibraryData roofLibrary;

	// Forecasts offered by the editor's Weather dropdown, in menu order. The
	// selected one overrides the zone-blended weather so a scene can be
	// authored under a chosen sky; the first entry is what the editor opens
	// with. Each preset's inspector "Resource Name" is its menu label.
	[ExportGroup("View")]
	[Export] public WeatherData[] weatherPresets = System.Array.Empty<WeatherData>();
}
