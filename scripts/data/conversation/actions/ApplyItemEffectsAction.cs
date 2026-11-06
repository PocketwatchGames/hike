using Godot;
using Godot.Collections;

// Runs ItemEffects on the player being spoken to, as if they had used them
// themselves: a healer's heal, a curse, waking from Unconscious
// (RemoveStatusEffectsEffect). Reuses the item-effect library so a conversation
// can do to the player anything a potion can, without an action type per verb.
//
// Not idempotent — gate a one-time effect like any other repeatable response.
[GlobalClass]
public partial class ApplyItemEffectsAction : ConversationAction
{
    [Export] public Array<ItemEffect> effects = new();

    public override void Execute(ConversationContext ctx)
    {
        ItemEffect.ApplyToSelf(ctx.player, effects);
    }
}
