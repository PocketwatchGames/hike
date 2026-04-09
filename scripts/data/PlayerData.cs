using Godot;

[GlobalClass]
public partial class PlayerData : Resource
{
	[Export] public float stepHeight = 0.5f;
	[Export] public float moveSpeed = 7f;
	[Export] public float sneakSpeed = 3f;
	[Export] public float jumpSpeed = 18f;
	[Export] public float visionRange = 25f;
	[Export] public float visibilityLightMax = 0.75f;
	[Export] public float visibilityMovementMin = 0.5f;
	[Export] public float visibilityMovementPower = 2f;
	[Export] public float perceptionMinimum = 0.01f;
	[Export] public float perceptionInstant = 0.5f;
	[Export] public float perceptionDetectedThreshold = 0.25f;
}
