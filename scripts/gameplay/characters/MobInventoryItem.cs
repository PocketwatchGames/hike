// Runtime entry in MobSimState.Inventory — one ItemState plus the per-item
// merchant flags from the authored MobInventoryData it was seeded from, kept
// off ItemState so it stays unaware of merchant-screen concerns. Per-mob stock
// can shrink (player buys the last sword) without mutating the shared
// authored MobInventoryData resource.
public class MobInventoryItem
{
    public ItemState item;
    public float loyaltyCost;
    public bool secret;
}
