using Godot;

// Drop-in node that fires a one-shot screen flash when it enters the tree.
// Author it as a child of any effect scene — an Fx particle burst, a sound
// one-shot, a hazard — to pair a screenspace flash with that effect (the
// fairy's death burst, a lightning strike, an explosion). The flash itself is
// owned and decayed by ScreenEffectsController, so this node can be freed the
// moment the burst finishes.
[GlobalClass]
public partial class ScreenFlashEmitter : Node
{
	[Export] public ScreenFlashData flash;
	// Fire automatically on spawn (the common case for an effect scene). Turn
	// off to drive it manually via Fire() from code.
	[Export] private bool _fireOnReady = true;

	public override void _Ready()
	{
		if (_fireOnReady)
		{
			Fire();
		}
	}

	public void Fire()
	{
		ScreenEffectsController.Current?.Flash(flash);
	}
}
