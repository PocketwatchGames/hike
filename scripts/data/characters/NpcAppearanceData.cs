using Godot;

// One NPC's whole look, authored in this one file: the rig, the outfit worn on
// it, the hair, the skin and the portrait.
//
// The outfit is the same inline OutfitData armour carries, so a look can be
// copied between an armour piece and an appearance in the inspector. Only the
// rig's own gender half is read (the look picks its rig); a part name the rig
// lacks is warned at spawn (ModelAnimator.ApplyAppearance) rather than silently
// drawing nothing.
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

    // Clothing (body, plus head for a hat, worn over the hair — pick a short
    // style, like the "b" cuts) and its dye. Required — the rig shows no body
    // without one.
    [Export] public OutfitData outfit;

    // Index into the rig's ModelAnimator.hairStyleMeshNames; out of range =
    // bald.
    [Export] public int hairStyle;
    // Defaults are the atlas swatches untinted hair and skin sample.
    [Export] public Color hairColor = OutfitData.DefaultSecondary;
    [Export] public Color skinTone = OutfitData.DefaultPrimary;

    // Face shown in the conversation panel while this NPC speaks. Null falls
    // back to the species' bestiary portrait.
    [Export] public Texture2D portrait;
}
