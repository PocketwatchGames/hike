// One loyalty reward a mob still holds (MobSimState.LoyaltyGifts), built at
// spawn from an authored LoyaltyGift. The item is already a real ItemState, so
// a level or mod authored on the gift reaches the player.
public class LoyaltyGiftState
{
    public ItemState item;
    public float requiredLoyalty;
}
