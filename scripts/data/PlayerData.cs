using Godot;

[GlobalClass]
public partial class PlayerData : Resource
{
	[Export] public float stepHeight = 0.5f;
	[Export] public float coyoteTime = 0.25f;
	[Export] public float moveSpeed = 7f;
	[Export] public float sneakSpeed = 3f;
	[Export] public float jumpSpeed = 18f;
	[Export] public float jumpHoldGravityScale = 0.65f;
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

	[Export] public int backpackCapacity = 20;
	[Export] public int consumableSlotCount = 3;

	// Scene used when the player drops an item from the inventory. Same Loot
	// scene the world drops use; we keep it on PlayerData so player drops
	// don't depend on which region they're standing in.
	[Export] public PackedScene dropLootScene;

	[Export] public float maxHealth = 100f;

	[Export] public float armorRechargeDelay = 3f;
	[Export] public float armorRechargeSpeed = 20f;
	[Export] public float armorRecoverTime = 8f;

	// Impulse the player applies to a mob when they run into it. Scaled
	// by the player's current horizontal speed and divided by the mob's
	// mass, so heavy mobs barely budge while light mobs scatter. 0 disables
	// player-pushes-mob entirely.
	[Export] public float mobPushStrength = 8f;
}
