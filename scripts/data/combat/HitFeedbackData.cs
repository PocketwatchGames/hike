using Godot;

// What the PLAYER feels when their attack lands on a creature: a screen flash and
// a camera shake, played once per swing. PlayerData.meleeHitFeedback is the
// default for every melee weapon; ItemEvent.impactFeedback overrides it per attack.
// [Tool] because ItemEvent (a [Tool] class) holds one by typed [Export].
[Tool]
[GlobalClass]
public partial class HitFeedbackData : Resource
{
	// Null = no flash.
	[Export] public ScreenFlashData screenFlash;
	// Meters of camera offset at peak; 0 = no shake.
	[Export(PropertyHint.Range, "0,2,0.01")] public float cameraShakeMagnitude = 0.12f;
	[Export(PropertyHint.Range, "0.02,2,0.01")] public float cameraShakeDuration = 0.12f;

	public void Play()
	{
		if (screenFlash != null)
		{
			ScreenEffectsController.Current?.Flash(screenFlash);
		}
		GameCamera cam = GameCamera.Current;
		if (cam != null && cameraShakeMagnitude > 0f)
		{
			// Range 0: the player is the camera's target, so full strength.
			cam.Shake.AddImpulse(cameraShakeMagnitude, cameraShakeDuration, Vector3.Zero, 0f, Vector3.Zero);
		}
	}
}
