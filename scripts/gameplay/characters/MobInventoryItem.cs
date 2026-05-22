// Runtime entry in MobSimState.Inventory — one ItemState plus the per-item
// flags from the authored MobInventoryData it was seeded from. The wrapper
// exists because ItemState carries the mutable stack count we need at
// runtime, but ItemState itself shouldn't know about merchant-screen-specific
// flags like loyaltyCost / secret. Per-mob stock can shrink (player buys the
// last sword) without mutating the shared authored MobInventoryData resource.
public class MobInventoryItem
{
    public ItemState item;
    public float loyaltyCost;
    public bool secret;
}
