using Godot;

// Reward authored against a loyalty threshold. When a mob's Loyalty crosses
// requiredLoyalty (via Mob.ReceiveGift), the gift is handed back to the
// player and consumed from the mob's remaining-gift list. A gift can be
// either an item stack or a language-component grant (or both) — author
// whichever fields apply and leave the rest at their defaults.
//
// Language-component gifts are skipped at evaluation time when the player
// already knows every component the gift would grant — see Mob.IsGiftRedundant.
// Item gifts are never auto-skipped, and the "Gift Received: <item>" HUD
// announcement is item-driven; a language-only gift fires the existing
// LanguageLearned announcement instead.
[GlobalClass]
public partial class LoyaltyGift : Resource
{
    // Optional item handed to the player. Stack size is `count`. Null = no
    // item portion (this gift is language-only).
    [Export] public ItemData item;
    [Export] public int count = 1;

    // Optional language teaching. When `language` is non-null and
    // `languageComponents` has at least one bit set, Mob.GiveLoyaltyGift
    // routes through Player.LearnLanguageComponents — the existing
    // LanguageLearned announcement covers the player-facing notice.
    [Export] public LanguageData language;
    [Export(PropertyHint.Flags)] public ELanguageComponents languageComponents = ELanguageComponents.None;

    // Loyalty value the mob must reach before this gift unlocks. Multiple
    // gifts can share a threshold; Mob.ReceiveGift hands out every gift the
    // crossing covers in authored order.
    [Export] public float requiredLoyalty = 1f;
}
