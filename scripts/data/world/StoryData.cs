using Godot;
using Godot.Collections;

// A full-screen story card: one picture over a few paragraphs of narration,
// shown paused until the player clicks through (StoryScreen). The text is
// narration, not in-world speech, so it is never language-scrambled.
[GlobalClass]
public partial class StoryData : Resource
{
    [Export] public Texture2D image;

    // One loc key per paragraph — a .tsv value is a single line, so a blank
    // line between paragraphs has to come from here.
    [Export] public Array<StringName> paragraphs = new();
}
