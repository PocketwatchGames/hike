using Godot;

// Items with loot-specific behavior — currently empty, but kept around as a
// distinct subclass so future loot-flavored fields (spoilageTime for berries,
// decay-into-nothing timers, drop-on-death triggers) have a home that doesn't
// pollute ItemData. Any plain ItemData can already be dropped in the world
// through the shared Loot scene; LootData exists for items that ALSO need
// world-on-ground dynamics beyond a basic pickup.
[GlobalClass]
public partial class LootData : ItemData
{
    // Lifetime of the world pickup in milliseconds. 0 = never expires (the
    // default, matches behavior before this field existed). When >0 the
    // Loot scene tracks elapsed time and, once the threshold is crossed,
    // plays the loot scene's removeFX and despawns. Used for foodstuffs
    // like raw meat that should rot off the ground if the player doesn't
    // grab them.
    [Export] public int removeTimeMs = 0;

    // Optional 3D model shown in place of the flat world sprite when this loot
    // is on the ground. Null (the default) keeps the standard Sprite3D pickup;
    // set it for loot whose authored identity is a mesh rather than an icon —
    // e.g. the crown, which reuses the elite mob's golden halo so the dropped
    // trophy reads identically to the marker the live elite carried. The model
    // supplies its own material / x-ray stack and any idle animation, so the
    // Loot scene hides its sprite and lets the model render itself. The
    // inventorySprite is still used for the backpack icon.
    [Export] public PackedScene worldModel;
}
