using Godot;

// A per-instance recolor palette for a mob, applied once at spawn. Lets one
// mob scene/FBX (e.g. goblin.tscn) serve many biome variants by recoloring its
// meshes instead of authoring a unique model per variant — the same
// composition-over-duplication approach the item system uses. Reference a
// MobPalette from a MobData variant; null leaves the mob's authored textures
// untouched.
[Tool]
[GlobalClass]
public partial class MobPalette : Resource
{
    // Applied in order; later entries win for any mesh named twice.
    [Export] public MobRecolorEntry[] recolors = System.Array.Empty<MobRecolorEntry>();
}
