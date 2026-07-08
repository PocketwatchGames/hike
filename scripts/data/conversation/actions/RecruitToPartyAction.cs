using Godot;

// Conversation side-effect that recruits the speaker into the player's party.
// Authored on a ConversationResponse (e.g. a "[Come with me.]" reply) that only
// shows once the NPC is willing to join — gate it with the conversation's own
// visibility conditions (a script flag, gifted loyalty, etc.).
//
// Requires ctx.speaker to be a recruitable Mob (its MobSimState carries a
// RecruitTemplate, seeded from NpcSpawnEntry.recruitTemplate). No-op otherwise —
// a non-Mob or a mob with no template has nothing to become a party member.
// GameClient.RecruitToParty clones the template into a new roster member who
// stands at the active campfire and despawns this mob, so a single traversal is
// terminal: the speaker is gone and this can't fire again.
[GlobalClass]
public partial class RecruitToPartyAction : ConversationAction
{
    public override void Execute(ConversationContext ctx)
    {
        if (ctx.speaker is not Mob mob || mob.RecruitTemplate == null)
        {
            return;
        }
        // Close the conversation panel first — its follow-up branch / chooser
        // would otherwise anchor to a mob we're about to despawn.
        ctx.controller?.Close();
        GameClient.Current?.RecruitToParty(mob);
    }
}
