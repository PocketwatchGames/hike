// One loyalty reward a mob still holds (MobSimState.LoyaltyGifts), built at
// spawn from an authored LoyaltyGift. The item is already a real ItemState, so
// a level or mod authored on the gift reaches the player.
public class LoyaltyGiftState
{
    // Null = no item portion (a language-only gift).
    public ItemState item;
    public LanguageData language;
    public ELanguageComponents languageComponents;
    public float requiredLoyalty;
}
