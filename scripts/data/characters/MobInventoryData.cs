using Godot;

// One entry in a mob's authored inventory. Pairs an item with a stock count
// and two optional flags: secret pulls the entry off the shop side entirely
// (a possession the mob carries for narrative reasons but never sells), and
// loyaltyCost is an authored "price in loyalty" tag that other systems
// (pricing, quest gates, dialogue) can read — it is NOT consulted by the
// MerchantScreen. At worldgen time each MobInventoryData is duplicated into
// a runtime MobInventoryItem on MobSimState so the per-instance stock can
// shrink without mutating the shared .tres.
[GlobalClass]
public partial class MobInventoryData : Resource
{
    [Export] public ItemData item;
    [Export] public int count = 1;
    // Authored loyalty price for this entry. Not enforced by the merchant
    // screen — purely data for downstream consumers.
    [Export] public float loyaltyCost = 0f;
    // When true, this entry never appears on the merchant's shop side. The
    // item still lives on the mob (so quest / narrative code can reference
    // it) but the trade UI behaves as if it isn't there.
    [Export] public bool secret = false;
}
