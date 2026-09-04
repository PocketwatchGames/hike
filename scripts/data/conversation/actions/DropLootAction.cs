using Godot;

// Conversation side-effect that makes the speaker drop an authored item list at
// their feet — a quest reward handed over, a bandit turning out their pockets,
// a corpse-to-be scattering what it carried. Authored on the response (or
// branch) where the handover actually happens.
//
// The items are the ACTION's, not the mob's: the speaker's species loot is
// untouched and still drops if it later dies. Requires ctx.speaker to be a Mob
// — no-op otherwise, since a non-Mob speaker has no eject arc to fire along.
//
// Not idempotent. A response the player can pick twice drops twice; gate it
// with the conversation's own visibility conditions (a script flag set by a
// SetScriptVarAction alongside this one) if it should only ever fire once.
[GlobalClass]
public partial class DropLootAction : ConversationAction
{
    // What the speaker drops. Each entry fires `count` separate Loot instances
    // on a random horizontal heading, so a multi-item handover scatters rather
    // than stacking on one tile.
    [Export] public Godot.Collections.Array<ItemCount> loot = new();

    public override void Execute(ConversationContext ctx)
    {
        if (ctx.speaker is not Mob mob)
        {
            return;
        }
        mob.EjectItems(loot);
    }
}
