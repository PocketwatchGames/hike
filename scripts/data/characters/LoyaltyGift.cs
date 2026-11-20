using Godot;

// Reward authored against a loyalty threshold. When a mob's Loyalty crosses
// requiredLoyalty (via Mob.ReceiveGift), the gift is handed back to the
// player and consumed from the mob's remaining-gift list. A gift is an item
// stack; a gift that teaches a language is a scroll, which teaches as it is
// picked up like any other.
[GlobalClass]
public partial class LoyaltyGift : Resource
{
    // The item handed to the player, with any level and mods. Stack size is
    // `count`.
    [Export] public ItemDescriptor item;
    [Export] public int count = 1;

    // Loyalty value the mob must reach before this gift unlocks. Multiple
    // gifts can share a threshold; Mob.ReceiveGift hands out every gift the
    // crossing covers in authored order.
    [Export] public float requiredLoyalty = 1f;

    // The runtime gift a mob holds — see LoyaltyGiftState.
    public LoyaltyGiftState CreateState()
    {
        ItemState state = item?.CreateState();
        state?.SetCount(Mathf.Max(1, count));
        return new LoyaltyGiftState
        {
            item = state,
            requiredLoyalty = requiredLoyalty,
        };
    }
}
