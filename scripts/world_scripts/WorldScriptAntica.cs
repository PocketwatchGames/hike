using Godot;

// Antica's scripted events.
[GlobalClass]
public partial class WorldScriptAntica : WorldScriptData
{
    // The painter placement name of the NPC fairy standing over the party at
    // the start.
    private const string IntroFairy = "intro_fairy";

    // The party wakes to the fairy talking to them.
    public override void OnNewGame(WorldScriptApi api)
    {
        api.StartConversation(IntroFairy);
    }
}
