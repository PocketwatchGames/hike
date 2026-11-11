using Godot;
using Godot.Collections;

// A full-screen story shown paused until the player clicks through each page
// (StoryScreen). The text is narration, not in-world speech, so it is never
// language-scrambled.
[GlobalClass]
public partial class StoryData : Resource
{
    [Export] public Array<StoryPageData> pages = new();
}
