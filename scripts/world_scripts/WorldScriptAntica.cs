using Godot;

// Antica's scripted events.
[GlobalClass]
public partial class WorldScriptAntica : WorldScriptData
{
    // The painter placement name of the NPC fairy standing over the party at
    // the start.
    private const string IntroFairy = "intro_fairy";

    // How long the party lies there before the fairy speaks.
    [Export(PropertyHint.Range, "0,10,0.1")] public float introConversationDelaySeconds = 1.5f;

    // The party wakes to the fairy talking to them.
    public override void OnNewGame(WorldScriptApi api)
    {
        api.After(introConversationDelaySeconds, a => a.StartConversation(IntroFairy));
    }
}
