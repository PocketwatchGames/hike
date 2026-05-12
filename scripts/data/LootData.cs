using Godot;

// Authored static data for a piece of loot on the ground. Carried by
// LootSimState; controls how the loot behaves when the player encounters it.
// Future fields (pickup sound, sparkle effect, glow color, rarity tier) hang
// here so spawn-site code can stay agnostic.
[GlobalClass]
public partial class LootData : Resource
{
    // Scene that materialises this loot in the world. Pairs with AutoPickup:
    // auto_loot.tscn for AutoPickup=true, loot.tscn for AutoPickup=false. The
    // pair lives on the same Resource so mis-authoring (e.g. an AutoLoot scene
    // with AutoPickup=false) isn't possible at the spawn-site level.
    [Export] public PackedScene Scene;

    // True: walking into the loot picks it up automatically (AutoLoot scene).
    // False: the player must press Interact to pick it up (Loot scene).
    [Export] public bool AutoPickup = true;
}
