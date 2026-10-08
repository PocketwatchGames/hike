using Godot;

// An item's inventory sprite floating up off a world point and fading — the
// "this was used" beat when an interaction spends an item (a key on a chest).
// Spawned by GameClient.ShowItemsUsed.
[GlobalClass]
public partial class HudItemIcon : HudFloater
{
	// Centred on the Control's origin in the scene, so the icon sits on its anchor.
	[Export] public TextureRect icon;

	public static void Create(PackedScene scene, Sim sim, Camera3D camera, Vector3 worldPosition, Vector2 screenOffset, Texture2D texture, Node parent)
	{
		var instance = scene.Instantiate<HudItemIcon>();
		if (instance.icon != null)
		{
			instance.icon.Texture = texture;
		}
		instance.Init(sim, camera, worldPosition, screenOffset, parent);
	}
}
