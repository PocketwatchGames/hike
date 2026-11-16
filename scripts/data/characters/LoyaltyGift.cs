using Godot;

// Reward authored against a loyalty threshold. When a mob's Loyalty crosses
// requiredLoyalty (via Mob.ReceiveGift), the gift is handed back to the
// player and consumed from the mob's remaining-gift list. A gift can be
// either an item stack or a language-lesson grant (or both) — author
// whichever fields apply and leave the rest at their defaults.
//
// Language gifts are skipped at evaluation time when the player already holds
// the lesson the gift would teach — see Mob.IsGiftRedundant.
// Item gifts are never auto-skipped, and the "Gift Received: <item>" HUD
// announcement is item-driven; a language-only gift fires the existing
// LanguageLearned announcement instead.
[GlobalClass]
public partial class LoyaltyGift : Resource
{
    // Optional item handed to the player, with any level and mods. Stack size is
    // `count`. Null = no item portion (this gift is language-only).
    [Export] public ItemDescriptor item;
    [Export] public int count = 1;

    // Optional language teaching: `languagePercent` of `language` under lesson
    // number `languageLesson` (see LanguageData), through
    // Player.LearnLanguageLesson — the LanguageLearned announcement covers the
    // player-facing notice. A zero percent teaches nothing.
    [Export] public LanguageData language;
    [Export] public int languageLesson;
    [Export(PropertyHint.Range, "0,100,1")] public float languagePercent;

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
            language = language,
            languageLesson = languageLesson,
            languagePercent = languagePercent,
            requiredLoyalty = requiredLoyalty,
        };
    }
}
