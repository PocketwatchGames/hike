using Godot;

// The HUD layer speech bubbles are spawned into, so any number can be up at
// once — a conversation's, a member's remark. Full-screen and input-ignoring:
// each bubble lays itself out in this space (see SpeechBubble).
[GlobalClass]
public partial class SpeechBubbles : Control
{
	[Export] PackedScene bubbleScene;

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
	}

	// A new bubble over `anchor`; it frees itself on Close or when the anchor dies.
	// `inscribed`: the text is carved or written on `anchor`, not spoken by it.
	public SpeechBubble Spawn(IHudAnchored anchor, bool inscribed)
	{
		if (bubbleScene?.Instantiate() is not SpeechBubble bubble)
		{
			GD.PushError("SpeechBubbles: bubbleScene must have a SpeechBubble root");
			return null;
		}
		AddChild(bubble);
		bubble.Open(anchor, inscribed);
		return bubble;
	}
}
