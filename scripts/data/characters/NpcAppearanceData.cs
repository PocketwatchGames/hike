using Godot;

// One NPC's look, as a single authored choice: the rig, the clothes and
// colours worn on it, the hair, and the skin — the same ingredients a party
// member's look is built from (PlayerState's skin / hair picks plus the
// OutfitData its armour names).
//
// The outfit is an OutfitData because it lists its parts for BOTH rigs and the
// rig picks its own (ModelAnimator.gender), so a look can never name meshes the
// rig doesn't have. The hair is an index into the rig's own hair menu for the
// same reason.
//
// Reusable by construction: two villagers in one appearance are two placements
// naming one file, so retuning the look retunes both. Give one its own variation
// by authoring another appearance, not by editing a placement's copy.
[GlobalClass]
public partial class NpcAppearanceData : Resource
{
    // The model scene instanced for an NPC wearing this appearance — the rig,
    // and with it the gender. Null falls back to the species' own
    // MobData.mobScene.
    [Export] public PackedScene scene;

    // Clothing parts (body, plus head for a hat) and their colours. Required —
    // the rig shows no body without one.
    [Export] public OutfitData outfit;

    // Index into the rig's ModelAnimator.hairStyleMeshNames; out of range =
    // bald.
    [Export] public int hairStyle;
    // Defaults are the atlas swatches untinted hair and skin sample.
    [Export] public Color hairColor = OutfitData.DefaultSecondary;
    [Export] public Color skinTone = OutfitData.DefaultPrimary;
}
