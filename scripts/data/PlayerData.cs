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
	[Export] public float VisionRangePower = 2f;
	[Export] public float visibilityMovementMin = 0.5f;
	[Export] public float visibilityMovementPower = 2f;
	[Export] public float perceptionMinimum = 0.01f;
	[Export] public float perceptionInstant = 0.75f;
	[Export] public float PerceptionRelaxationSpeed = 0.1f;
	[Export] public float PerceptionIncreaseSpeed = 0.25f;
	// Per-sense multipliers applied to the vision / hearing perception delta
	// before they're summed and accumulated. Mirrors MobData; the player's
	// perception of mobs (PlayerPerception.Tick) uses these.
	[Export] public float VisionStrength = 1f;
	[Export] public float HearingStrength = 1f;
	// Hearing reach scalar. A sound of `decibels` is heard if
	// `decibels * hearingRange > distance`. The player's state transitions
	// (Hidden→Detected→Discovered) still require active visual contact —
	// hearing primes perception but can't cross the threshold alone.
	[Export] public float hearingRange = 10f;
	[Export] public float hearingRangePower = 0.5f;
	// Continuous movement noise the player emits. Mapped piecewise: 0 at
	// rest, sneakDecibels at sneakSpeed, runDecibels at moveSpeed. Mobs
	// sample this in their mob-perceives-player tick.
	[Export] public float sneakDecibels = 1f;
	[Export] public float runDecibels = 6f;

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

	// LootData used when the player drops an item from the inventory. Carries
	// both the loot scene and the auto-pickup flag; kept on PlayerData so
	// player drops don't depend on which zone they're standing in.
	[Export] public LootData dropLootData;

	[Export] public float maxHealth = 100f;

	[Export] public float armorRechargeDelay = 3f;
	[Export] public float armorRechargeSpeed = 20f;
	[Export] public float armorRecoverTime = 8f;

	// Impulse the player applies to a mob when they run into it. Scaled
	// by the player's current horizontal speed and divided by the mob's
	// mass, so heavy mobs barely budge while light mobs scatter. 0 disables
	// player-pushes-mob entirely.
	[Export] public float mobPushStrength = 8f;

	// Degrees F per second that bodyTemperature drifts toward the sampled
	// environmental temperature. Lower = more inertia (a brief gust through
	// a cold zone won't trigger Cold); higher = the player tracks ambient
	// changes more responsively.
	[Export] public float temperatureAcclimationSpeed = 5f;

	// Body-temperature thresholds in degrees F. Below coldTemperature the
	// player gains coldStatus; above hotTemperature they gain hotStatus.
	// Returning to the safe band arms the status's normal duration timer
	// (5s on the authored cold/hot resources) for removal.
	[Export] public float coldTemperature = 50f;
	[Export] public float hotTemperature = 90f;

	[Export] public StatusEffectData coldStatus;
	[Export] public StatusEffectData hotStatus;

	// Degrees F shifted onto BOTH thresholds per m/s of sampled wind speed.
	// Wind chills the player: positive values raise both thresholds, making
	// cold trigger at warmer ambient and hot harder to reach. GameClient.
	// SampleWindSpeed already drops to 0 under overhead shelter so caves and
	// covered structures don't fake a draft.
	[Export] public float windTemperatureReduction = 0.5f;
}
