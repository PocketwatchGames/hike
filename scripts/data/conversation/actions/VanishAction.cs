using Godot;

// The speaker rises, fades out and stays hidden until a world script brings it
// back (WorldScriptApi.Reappear) — the escape a story needs, where the fairy's
// own Escape behavior removes her for good. Named in a sheet's action cell as
// `vanish` (dialogue_scripts/actions/vanish.tres).
//
// Requires ctx.speaker to be a Mob; errors otherwise.
[GlobalClass]
public partial class VanishAction : ConversationAction
{
    public override void Execute(ConversationContext ctx)
    {
        if (ctx.speaker is not Mob mob)
        {
            GD.PushError("VanishAction: the speaker is not a mob, so it cannot vanish.");
            return;
        }
        mob.Vanish();
    }
}
