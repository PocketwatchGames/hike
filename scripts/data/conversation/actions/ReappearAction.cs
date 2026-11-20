using Godot;

// The speaker vanishes and fades back in at a named place — the sheet's
// `reappear:<place>` cell. The place is any named painter placement (a
// `waypoint` is one made for the purpose), looked up in the world's points of
// interest, so a typo is an error when the action fires.
//
// Requires ctx.speaker to be a Mob; errors otherwise.
[GlobalClass]
public partial class ReappearAction : ConversationAction
{
    [Export] public StringName place;

    public override void Execute(ConversationContext ctx)
    {
        if (ctx.speaker is not Mob mob)
        {
            GD.PushError($"ReappearAction: the speaker is not a mob, so it cannot reappear at '{place}'.");
            return;
        }
        if (!WorldScriptApi.TryPlace(ctx.sim, place, out Vector3 at))
        {
            return;
        }
        mob.Reappear(at);
    }
}
