using Godot;

// Authored configuration for one buried-item spot. A spot is a thin marker in
// the world that, when dug up with a shovel, yields a payload (chest / loot /
// mob) and leaves a dirt mound behind. The same shared scene + this data drive
// every variant — what differs is the payload and the optional visuals.
//
// Remember-vs-forget is NOT a flag here. It is decided by WHERE the spot is
// placed: a spot authored into the persistent world (editor / .hike file)
// survives forever and its excavated state round-trips through save/load (the
// "buried treasure chest stays dug" case); a spot scattered by worldgen
// (BuriedSpotSpawnEntry) is re-rolled whenever its chunk regenerates, so a dug
// carrot is simply forgotten and regrows. Both share this exact data shape and
// the same BuriedSpotSimState — see EntitySerializer (Tag.BuriedSpot), which
// always persists the Excavated flag.
[GlobalClass]
public partial class BuriedSpotData : Resource
{
    // What digging this spot yields. Reuses the worldgen spawn vocabulary —
    // ChestSpawnEntry / LootSpawnEntry / MobSpawnEntry — so a buried spot can
    // produce anything worldgen can place, with no payload-specific code here.
    // The entry is rolled at DIG time (not placement time), so loot counts and
    // chest contents stay sealed until the player actually excavates. Null is
    // allowed (an empty hole) but pointless in practice.
    [Export] public SpawnEntryData payload;

    // Loot ejected on dig, popping out in a spray exactly like a chest's
    // contents (reuses Sim.EjectLootPile). Each ItemCount is one stacked pile.
    // This is the lightweight "dig -> loot pops out" outcome; the heavier
    // `payload` above is for digs that spawn an entity (a chest, forge, or mob).
    // A spot can carry both — the eject and the spawn both fire.
    [Export] public ItemCount[] loot = System.Array.Empty<ItemCount>();

    // Optional above-ground tell instanced under the spot's model anchor —
    // carrot tops, disturbed soil, a protruding chest corner. Null = no
    // indication at all: the player must dig blind to find it (buried
    // treasure). Purely cosmetic; the shovel finds the spot by proximity, not
    // by this visual.
    [Export] public PackedScene surfaceHintScene;

    // Mound left in the ground after the spot is dug. Swaps in where the hint
    // was. Null = the spot renders nothing once excavated (a carrot hole that
    // visually vanishes). A persistent treasure spot authors a satisfying
    // mound here so the looted spot stays marked.
    [Export] public PackedScene dirtPileScene;

    // One-shot audio-visual fired at the spot the moment the dig completes
    // (soil burst + thud). Spawned through Fx, so author it as an Fx scene.
    // Null = silent dig. This is a per-spot accent; the shovel's own Dig event
    // also plays a result-class effect (see resultClass) for the shared
    // "found nothing / common / treasure" feedback.
    [Export] public PackedScene digEffect;

    // Which result-class feedback the shovel plays when this spot is dug —
    // a carrot reads Common, a buried chest reads Treasure. Maps to the
    // shovel Dig event's digCommonEffect / digTreasureEffect.
    [Export] public EDigResult resultClass = EDigResult.Common;
}
