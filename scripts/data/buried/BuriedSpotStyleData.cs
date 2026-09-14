using Godot;

// What KIND of buried spot this is — how it reads above ground, what it leaves
// behind, and how digging it feels. Shared: a carrot patch and an unmarked
// treasure are two of these, reused by every spot of that kind.
//
// What is IN the ground is not here. It is per spot — which scroll, which
// chest — so it lives on the BuriedSpotSpawnEntry, which a painter placement
// forks and a spawn list names. Keeping it on this shared file meant burying
// anything new cost a new style file per treasure.
//
// Read at runtime (BuriedSpot instances the hint and mound from it, and a baked
// .hike references it by path), so it is shipped data in resources/data/buried/,
// not authoring vocabulary.
[GlobalClass]
public partial class BuriedSpotStyleData : Resource
{
    // Optional above-ground tell instanced under the spot's model anchor —
    // carrot tops, disturbed soil. Null = no indication at all: the player must
    // dig blind to find it. Purely cosmetic; the shovel finds the spot by
    // proximity, not by this visual.
    [Export] public PackedScene surfaceHintScene;

    // Mound left in the ground after the spot is dug, in place of the hint.
    // Null = nothing is drawn once excavated.
    [Export] public PackedScene dirtPileScene;

    // One-shot audio-visual fired at the spot the moment the dig completes.
    // Spawned through Fx, so author it as an Fx scene. The shovel's own Dig
    // event also plays the result-class feedback (see resultClass).
    [Export] public PackedScene digEffect;

    // Which result-class feedback the shovel plays when this spot is dug — a
    // carrot reads Common, a buried chest reads Treasure. Maps to the shovel Dig
    // event's digCommonEffect / digTreasureEffect.
    [Export] public EDigResult resultClass = EDigResult.Common;
}
