using Godot;

// One screen of a StoryData: a picture over a block of narration. The text is a
// loc key; StoryScreen crossfades from one page to the next on input.
[GlobalClass]
public partial class StoryPageData : Resource
{
    // Null = a text-only page.
    [Export] public Texture2D image;

    [Export] public StringName text;
}
