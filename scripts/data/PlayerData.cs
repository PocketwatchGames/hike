using Godot;

[GlobalClass]
public partial class PlayerData : Resource
{
	[Export] public float stepHeight = 0.5f;
	[Export] public float coyoteTime = 0.25f;
	[Export] public float moveSpeed = 7f;
	[Export] public float sneakSpeed = 3f;
	[Export] public float jumpSpeed = 18f;
	[Export] public float jumpHoldGravityScale = 0.5f;
	[Export] public float visionRange = 25f;
	[Export] public float VisionDistancePower = 2f;
	[Export] public float visibilityLightMax = 0.75f;
	[Export] public float visibilityMovementMin = 0.5f;
	[Export] public float visibilityMovementPower = 2f;
	[Export] public float perceptionMinimum = 0.01f;
	[Export] public float perceptionInstant = 0.75f;
	[Export] public float detectedThreshold = 0.1f;
	[Export] public float PerceptionRelaxationSpeed = 0.1f;
	[Export] public float PerceptionIncreaseSpeed = 0.25f;

	[Export] public float shallowWaterSpeed = 0.5f;
	[Export] public float swimSpeed = 3.5f;
	[Export] public float swimVerticalSpeed = 4f;
	[Export] public float waterSinkSpeed = 2f;
	[Export] public float buoyancyAcceleration = 15f;
	[Export] public float waterDrag = 5f;
	[Export] public float waterSurfaceOffset = 1f;
	[Export] public float waterJumpOffset = 1.5f;
}
