using Godot;

// One kind of full-screen flash: its color, peak, fade and SHAPE. Played through
// ScreenEffectsController.Flash, which gives each flash its own slot so a faint
// weapon flash landing during a fairy's death doesn't recolor or cut it short.
// [Tool] because ItemEvent (a [Tool] class) holds one by typed [Export].
[Tool]
[GlobalClass]
public partial class ScreenFlashData : Resource
{
	[Export] public Color color = new Color(1f, 1f, 1f, 1f);
	[Export(PropertyHint.Range, "0,1,0.01")] public float intensity = 0.5f;
	// Seconds from peak to nothing, linear.
	[Export(PropertyHint.Range, "0.02,2,0.01")] public float fadeSeconds = 0.3f;

	[ExportGroup("Shape")]
	// 0 washes the whole screen evenly; 1 lights only the rim beyond `edgeRadius`,
	// leaving the centre (where the player is aiming) clear.
	[Export(PropertyHint.Range, "0,1,0.01")] public float edgeAmount = 0f;
	// Where the rim starts, as a fraction of the centre-to-corner distance.
	[Export(PropertyHint.Range, "0,1.5,0.01")] public float edgeRadius = 0.5f;
	// Width of the ramp from `edgeRadius` to full rim strength.
	[Export(PropertyHint.Range, "0.01,1.5,0.01")] public float edgeSoftness = 0.5f;
}
