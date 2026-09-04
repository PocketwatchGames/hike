using Godot;

// Conversation side-effect that kills the speaker outright — a captive's last
// words, an assassination the player agrees to, a summoned thing unmaking
// itself. Closes the conversation first, so the branch or response chooser that
// would have followed is suppressed and the panel doesn't hang over a corpse.
//
// Terminal by construction: Mob.Kill runs the full death sequence (species loot
// ejection, death fx / VO, corpse physics) and clears the persisted Alive flag,
// and Mob.CanInteract gates on it — so the corpse offers no Talk verb and this
// can never fire a second time. Pair it with a DropLootAction ordered BEFORE
// this one when the NPC should also hand over something on the way out.
//
// Requires ctx.speaker to be a Mob. No-op otherwise.
[GlobalClass]
public partial class KillSpeakerAction : ConversationAction
{
    public override void Execute(ConversationContext ctx)
    {
        if (ctx.speaker is not Mob mob)
        {
            return;
        }
        // Close before killing — the follow-up branch / chooser would otherwise
        // anchor to a mob that's already a corpse.
        ctx.controller?.Close();
        mob.Kill();
    }
}
