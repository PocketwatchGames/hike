using Godot;

// Drop-in node that fires a one-shot screen flash when it enters the tree.
// Author it as a child of any effect scene — an Fx particle burst, a sound
// one-shot, a hazard — to pair a screenspace flash with that effect (the
// fairy's death burst, a lightning strike, an explosion). The flash itself is
// owned and decayed by ScreenEffectsController, so this node can be freed the
// moment the burst finishes. Color, intensity, and fade are tunable per scene.
[GlobalClass]
public partial class ScreenFlashEmitter : Node
{
	[Export] public Color color = new Color(1f, 1f, 1f, 1f);
	[Export(PropertyHint.Range, "0,1,0.01")] public float intensity = 0.5f;
	// Seconds for the flash to fade from peak to nothing. <= 0 uses the
	// ScreenEffectsController default.
	[Export(PropertyHint.Range, "0,2,0.05")] public float fadeSeconds = 0.3f;
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
		ScreenEffectsController.Current?.Flash(color, intensity, fadeSeconds);
	}
}
