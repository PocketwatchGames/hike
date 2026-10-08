using Godot;

// Floating world-space damage / heal / info number. Per-type scenes (one per
// EHudTextType) bake fade duration and vertical movement as exports; color
// is authored directly on the Label inside each scene and cascades through
// the Control's Modulate untouched. GameClient picks which scene to instance
// from EHudTextType.
[GlobalClass]
public partial class HudText : HudFloater
{
	[Export] public Label label;

	public static void Create(PackedScene scene, Sim sim, Camera3D camera, Vector3 worldPosition, string text, Node parent)
	{
		var hudText = scene.Instantiate<HudText>();
		if (hudText.label != null)
		{
			hudText.label.Text = text;
		}
		hudText.Init(sim, camera, worldPosition, Vector2.Zero, parent);
	}
}
