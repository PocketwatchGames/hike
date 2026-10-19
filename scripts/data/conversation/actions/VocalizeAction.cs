using Godot;

// The speaker voices a short non-verbal sound (their VoiceData.talk), cutting
// off any it is still making. Silent for a speaker with no talk clip.
[GlobalClass]
public partial class VocalizeAction : ConversationAction
{
    public override void Execute(ConversationContext ctx)
    {
        ctx.controller?.Vocalize();
    }
}
