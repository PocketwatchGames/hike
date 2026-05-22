using Godot;

// Conversation side-effect that closes the conversation panel and opens the
// speaker's merchant screen. Authored on a ConversationResponse (or
// ConversationEntry, less commonly) when the player should be handed off
// from dialogue to the buy/sell UI — e.g. a "[Show me your wares.]" response
// on a shopkeeper's greeting branch.
//
// Requires ctx.speaker to be a Mob (the merchant). No-op otherwise — a
// non-Mob speaker has nothing for MerchantScreen.Open to bind to.
[GlobalClass]
public partial class OpenShopAction : ConversationAction
{
    // False opens the screen in buy/sell mode; true opens it in gift mode
    // (player hands items to the merchant for loyalty). Matches
    // MerchantScreen.Open's `gifting` parameter and the existing Trade /
    // GiveItem action-verb split on Mob.
    [Export] public bool trading = true;

    public override void Execute(ConversationContext ctx)
    {
        if (ctx.speaker is not Mob mob)
        {
            return;
        }
        // Close the conversation panel first so it doesn't overlay the
        // merchant screen — the controller's _onClose still fires on the
        // caller normally.
        ctx.controller?.Close();
        mob.OpenMerchantScreen(trading, onClose: null);
    }
}
