using Godot;

[GlobalClass]
public partial class PlayerData : Resource
{
	// Per-EAnimation binding from logical slot to SpriteFrames clip name plus
	// retiming policy. Empty slots resolve to default-StringName and the
	// animator silently skips them — author the dictionary in the .tres to
	// wire each slot to its concrete clip. See AnimationData.
	[Export] public Godot.Collections.Dictionary<EAnimation, AnimationData> animations = new();

	// Look up the SpriteFrames clip name for an EAnimation slot. Returns
	// default StringName when the slot is unbound — callers route this
	// through LitSpriteAnimator.Play / HasAnimation, both of which no-op
	// on unknown names, so an unbound slot is a silent skip rather than a
	// hard error.
	public StringName GetAnimationName(EAnimation anim)
	{
		return animations.TryGetValue(anim, out AnimationData d) && d != null ? d.name : default;
	}

	// Returns whether the slot is authored to track statusAnimMul. Returns
	// false for unbound slots — playing nothing at status-retimed speed is
	// the same as playing nothing at authored speed.
	public bool IsAnimationSpeedAffected(EAnimation anim)
	{
		return animations.TryGetValue(anim, out AnimationData d) && d != null && d.affectedBySpeedMultiplier;
	}

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

	// Scent trail authoring. The player drops timestamped breadcrumbs that
	// advect with wind (per-crumb, voxel-collided) and decay linearly toward
	// zero strength. Lifetime is implicit: lifetime = strength / decayRate,
	// so a stronger emission lasts proportionally longer. Mobs sample the
	// crumb list in their mob-perceives-player tick (LOS-gated by raycast).
	[Export] public float scentStrength = 1f;
	[Export] public float scentDecayRate = 0.1f;
	[Export] public float scentStampInterval = 0.5f;
	[Export] public float scentStampMoveDistance = 1f;
	[Export] public int scentMaxCrumbs = 20;

	[Export] public float shallowWaterSpeed = 0.5f;
	[Export] public float swimSpeed = 3.5f;
	[Export] public float swimVerticalSpeed = 4f;
	[Export] public float waterSinkSpeed = 2f;
	[Export] public float buoyancyAcceleration = 15f;
	[Export] public float waterDrag = 5f;
	[Export] public float waterSurfaceOffset = 1f;
	[Export] public float waterJumpOffset = 1.5f;
	[Export] public float swimJumpSpeed = 8f;
	// Fraction of the local water current's velocity added to the player's
	// horizontal velocity each tick while swimming. Input is applied first
	// each tick and this layers on top, so 1.0 means the player drifts at
	// exactly the current's m/s when standing still and swims across-the-
	// current at input+current. Values above 1 amplify the push (debris in
	// rapids); below 1 lets a strong swimmer fight the flow.
	[Export] public float waterCurrentDrag = 1f;

	[Export] public int backpackCapacity = 20;
	[Export] public int consumableSlotCount = 3;

	[Export] public float maxHealth = 100f;

	// Maximum angle (radians) between the mob's facing direction and the
	// player→mob vector at hit time for the attack to count as a backstab.
	// A backstab requires the mob to be untriggered (still unaware of the
	// player); when both conditions hold, Mob.Hit folds OnBackstab modifiers
	// onto the live hit. ~45° (Pi/4) is the default.
	[Export] public float backstabAngle = 0.785f;

	[Export] public float armorRechargeDelay = 3f;
	[Export] public float armorRechargeSpeed = 20f;
	[Export] public float armorRecoverTime = 8f;

	// "Blood mana" drain regen, modeled on armor: every TryDrainBlood call
	// pushes _bloodRegenStartMs forward by bloodRegenDelay seconds, then
	// _drainedHealth refunds at bloodRegenSpeed HP/sec once the delay
	// elapses. A single shared delay (not per-drain) keeps the system
	// armor-simple — staggered drains during the delay all wait for the
	// same start time, after which the whole accumulated pool drains at
	// the flat rate.
	[Export] public float bloodRegenDelay = 3f;
	[Export] public float bloodRegenSpeed = 10f;

	// Stamina drains as the player performs effortful actions and refills on
	// its own. After any spend, recharge is gated for `staminaRechargeDelay`
	// seconds; once it begins, the bar refills from 0 to maxStamina over
	// `staminaRechargeTime` seconds (a flat rate, so partial spends refill
	// proportionally faster).
	[Export] public float maxStamina = 100f;
	[Export] public float staminaRechargeDelay = 1.5f;
	[Export] public float staminaRechargeTime = 3f;

	// Dash. The motion itself (speed / duration / freeze-gravity) is authored
	// on the dashActionProfile's ApplyMotion event so weapons / mob lunges can
	// reuse the same event shape with different tuning. The fields here are
	// player-side gates and post-dash behavior the runner doesn't model.
	[Export] public ItemActionProfile dashActionProfile;
	// Activation gates. Stamina is deducted unconditionally on press (allowed
	// to go negative); the only gate is that current stamina must be > 0 at
	// press time. dashCooldown is per-actor since dash isn't an inventory
	// item (so ItemAction.cooldownSeconds doesn't apply). dashMaxFallSpeed
	// prevents using dash to halt a long fall: pressing Dash while falling
	// faster than this is dropped silently.
	[Export] public float dashStaminaCost = 25f;
	[Export] public float dashCooldown = 0.35f;
	[Export] public float dashMaxFallSpeed = 8f;
	// Underwater speed scalar applied to the dash event's motionSpeed. 1.0
	// matches dry land; lower values give a slower swim-dash so the player
	// can't rocket through water.
	[Export] public float dashSwimSpeedScale = 0.5f;
	// Post-dash glide. When _dashTimeRemaining hits zero, _dashGlideRemaining
	// starts at dashGlideTime and counts down; while > 0 the player's
	// horizontal velocity is held at dashEndSpeedCap in the dash direction
	// (tapered linearly) instead of snapping to input speed. Lets the dash
	// carry momentum without leaving the player permanently fast.
	[Export] public float dashGlideTime = 0.15f;
	[Export] public float dashEndSpeedCap = 7f;
	// Wall handling during dash. After MoveAndSlide the player iterates
	// collisions: if the dash direction is within dashWallHeadOnAngle of
	// the wall normal (radians), the dash short-circuits (head-on bonk).
	// Otherwise the dash direction is reprojected onto the wall plane so
	// the player slides along it at full speed.
	[Export] public float dashWallHeadOnAngle = 0.785f;

	// Sprint: continuous movement modifier engaged by holding Dash past the
	// initial dash burst. Sprint is intent-based (any hold + move input);
	// stamina gates the speed boost but not the intent — holding Dash with
	// depleted stamina drops to moveSpeed but still arms the recharge delay,
	// so the player can't refill while gripping the sprint button.
	[Export] public float sprintSpeed = 12f;
	[Export] public float sprintStaminaDrainPerSecond = 15f;

	// Fallback speeds when stamina runs out and the player isn't actively
	// sprinting. tiredRunSpeed is the on-foot "exhausted run" — slower than
	// moveSpeed; tiredSwimSpeed is the swim equivalent. Sprinting with empty
	// stamina uses moveSpeed (not tiredRunSpeed) so the player is rewarded
	// for the effort while paying the recharge-delay cost.
	[Export] public float tiredRunSpeed = 4.5f;
	[Export] public float tiredSwimSpeed = 2f;

	// Continuous stamina drain (per second) while swimming and trying to move.
	// Stamina is allowed to go negative — movement is never gated on it, but
	// each tick of swim drain re-arms the recharge delay so the bar can't
	// refill until the player stops swimming or stops giving move input.
	[Export] public float swimStaminaDrainPerSecond = 10f;

	// Multiplier on the player's horizontal speed used as the *cap* on a
	// pushed mob's resulting velocity along the push direction. 1.5 = a
	// mob the player runs into ends up moving 1.5× the player's speed at
	// most, regardless of how many physics ticks contact lasts. 0 disables
	// player-pushes-mob entirely. Mass-independent — heavier mobs no longer
	// resist proportionally, since Player.PushTouchedMobs scales the impulse
	// by mob.Mass so the velocity change is the same for any mass.
	[Export] public float mobPushStrength = 1.5f;

	// Multiplier on player speed for the lateral "slip" cap — when the
	// player grazes the side of a mob's capsule, the mob is nudged
	// sideways out of the path proportional to how off-center the
	// contact was. A dead-center hit produces no slip. 0 disables the
	// slip entirely (pure forward push).
	[Export] public float mobPushSlip = 1.0f;

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

	// Player accumulates wetness in [0, 1] while exposed to rain or in
	// water; the wet status only arms once it crosses wetnessArmThreshold,
	// and only releases once it falls below wetnessDisarmThreshold
	// (hysteresis prevents the status flapping when wetness hovers near
	// one boundary). Standing in water snaps wetness to 1 immediately —
	// you're soaked the moment you step in.
	//
	// Rates are units-per-second of wetness with the input at full
	// strength. wetnessRainRate of 0.02 means it takes ~25 seconds in
	// full RainIntensity = 1 rain to cross the 0.5 arm threshold (and
	// ~50 seconds to fully saturate). Drying inversely scales with
	// temperature — a warm dry day dries faster — so wetnessDryRate is
	// the *baseline* rate at neutral conditions; warmth zones override
	// it via wetnessWarmthDryRate.
	[Export(PropertyHint.Range, "0,1,0.001")] public float wetnessRainRate = 0.02f;
	[Export(PropertyHint.Range, "0,1,0.001")] public float wetnessDryRate = 0.003f;
	[Export(PropertyHint.Range, "0,1,0.001")] public float wetnessWarmthDryRate = 0.1f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float wetnessArmThreshold = 0.5f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float wetnessDisarmThreshold = 0.1f;
}
