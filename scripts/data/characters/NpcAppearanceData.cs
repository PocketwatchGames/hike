using Godot;

// One NPC's look, as a single authored choice: the rig, the outfit meshes shown
// on it, and the recolor applied to them.
//
// The three are bundled because they are NOT independent — an outfit names
// meshes that exist only in a particular rig ("F_Archer_Top" is in the female
// villager scene and nowhere else), and a recolor names those same meshes
// again. Offered as three separate fields, the only thing stopping a male rig
// wearing a female outfit is the author remembering, and the result fails
// silently: the meshes simply do not resolve and the NPC spawns in its rig's
// default clothes.
//
// So this is what a placement editor picks, one row, and a mismatch is
// unrepresentable rather than merely discouraged. It is also why NpcSpawnEntry's
// raw scene / outfit / palette trio stays hidden from that editor — see
// SpawnEntryData.IsIdentityProperty.
//
// Reusable by construction: two villagers in one appearance are two placements
// naming one file, so retuning the look retunes both. Give one its own variation
// by authoring another appearance, not by editing a placement's copy.
[GlobalClass]
public partial class NpcAppearanceData : Resource
{
    // The model scene instanced for an NPC wearing this appearance — the rig,
    // and with it the gender the outfit has to match. Null falls back to the
    // species' own MobData.mobScene.
    [Export] public PackedScene scene;

    // The rig's visible clothing / hair / hat mesh names, composed with its
    // always-on base meshes at spawn. Empty = the scene's authored default.
    [Export] public string[] outfit = System.Array.Empty<string>();

    // Tints applied to those meshes, so two NPCs in one outfit still read as
    // distinct. Null = no recolor.
    [Export] public MobPalette palette;
}
