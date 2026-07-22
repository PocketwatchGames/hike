using Godot;

[GlobalClass]
public partial class PlayerData : Resource
{
	// The player's BASE (unarmed) animation set — the slot→clip mapping plus the
	// speed-affected / hides-held-item slot classifications. Lifted out of
	// PlayerData into a shared WeaponAnimSet so the unarmed loadout is just another
	// set, co-located with the source art. A wielded weapon's WeaponData.animSet
	// overrides individual slots over this base (see Player.AnimName).
	[Export] public WeaponAnimSet baseAnims;

	// Clip name for an EAnimation slot from the base set, or default when unbound
	// (the animator no-ops on unknown names, so an unbound slot is a silent skip).
	public StringName GetAnimationName(EAnimation anim)
	{
		return baseAnims != null ? baseAnims.GetOverride(anim) : default;
	}

	// Whether the slot retimes with slow/haste status (base-set classification).
	public bool IsAnimationSpeedAffected(EAnimation anim)
	{
		return baseAnims != null && baseAnims.IsSpeedAffected(anim);
	}

	// Whether the currently-playing clip conceals the wielded weapon (drink / read
	// / cast). Keyed by clip name so HeldItemVisual can test the animator's current
	// clip directly; the hides poses are base clips, never weapon-overridden.
	public bool AnimationHidesHeldItem(StringName clipName)
	{
		return baseAnims != null && baseAnims.HidesHeldItemClip(clipName);
	}

	[ExportGroup("Movement")]
	[Export] public float stepHeight = 0.5f;
	[Export] public float coyoteTime = 0.25f;
	[Export] public float moveSpeed = 7f;
	[Export] public float sneakSpeed = 3f;
	// How strongly foliage (bushes, tall grass) slows the player. The foliage's
	// own speed multiplier applies at full strength at 1, is ignored at 0, and
	// partially at intermediate values. Mirrors MobData.foliageSpeedModifier.
	[Export(PropertyHint.Range, "0,1,0.01")] public float foliageSpeedModifier = 1f;
	// Slope-based locomotion bonus/penalty AT the steepest walkable slope
	// (FloorMaxAngle). Running straight downhill speeds up by up to
	// downhillSpeedBonus, straight uphill slows by up to uphillSpeedPenalty;
	// gentler slopes and off-axis headings scale linearly toward 1 (flat / level
	// traverse). Folded into the terrain speed scalar, so footstep cadence
	// retimes with it too.
	[Export(PropertyHint.Range, "0,1,0.01")] public float downhillSpeedBonus = 0.15f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float uphillSpeedPenalty = 0.25f;
	// Shapes how the bonus/penalty ramp from flat to the max slope. The grade
	// fraction (0 flat → 1 at max slope) is raised to this power before scaling
	// the cap. <1 eases OUT — shallow slopes already feel most of the effect and
	// it flattens toward the top; 1 = linear. Lower = the change reads sooner on
	// gentle ground.
	[Export(PropertyHint.Range, "0.2,1,0.05")] public float slopeSpeedEaseExponent = 0.5f;
	// Linear horizontal acceleration (m/s²) toward the input target. Ground is
	// sharp — most games snap; we ramp just enough to smooth the transition
	// between speeds without making input feel floaty. Air is drifty so jumps
	// preserve momentum; lower values lengthen the ramp. Same approach math as
	// waterAcceleration, but air/ground targets are pure input (no drift term).
	[Export] public float groundAcceleration = 50f;
	[Export] public float airAcceleration = 12f;
	// Acceleration substituted for groundAcceleration while the player is
	// in a skid (sharp direction change with |inputVel − currentXZ| over
	// the skid-enter threshold). Lower than groundAcceleration so velocity
	// commits to the pre-snap direction for ~0.5s instead of snapping in
	// ~0.15s. Once the skid exits, full groundAcceleration is restored.
	[Export] public float skidGroundAcceleration = 15f;
	// Skid-state hysteresis thresholds on |inputVel − currentXZ| (m/s).
	// Enters when the gap exceeds skidEnterSpeed, exits when it drops below
	// skidExitSpeed. Keep exit < enter to prevent flicker at the boundary.
	[Export] public float skidEnterSpeed = 14f;
	[Export] public float skidExitSpeed = 7f;
	// Airborne drag, split by axis (skipped during dash).
	//
	// airDragDown only fights *downward* motion (Velocity.Y < 0): upward
	// jumps and launches are unaffected, but a fall's terminal speed is
	// bounded by Gravity / airDragDown (≈ 9.8 m/s at airDragDown = 1).
	// Linear coefficient (1/s).
	//
	// airDragXZ is a QUADRATIC coefficient (1/m). Per-tick horizontal
	// deceleration scales as airDragXZ * |v_rel|² along v_rel, where
	// v_rel = velocityXZ − wind drift. Picking a small value keeps drag
	// imperceptible at sprint speeds and very strong past them — at
	// k = 0.02 the decel at sprint (~11 m/s) is ~2.4 m/s² (subtle) while
	// 2× sprint hits ~10 m/s² (firm), so skate / dash-launch excess speed
	// bleeds off hard while sustained-input top speed is essentially
	// untouched. Computed in the WIND-RELATIVE frame so a player at rest
	// in strong wind drifts to the wind's drift target rather than zero
	// (see windDragXZ).
	[Export] public float airDragDown = 1f;
	[Export] public float airDragXZ = 0.02f;
	// Wind pickup factor in [0, 1]. Wind drift target = sampled wind ×
	// windDragXZ; that target is added to the input vector each airborne
	// tick (input STACKS on wind, so player intent is never fought) AND
	// is the asymptote of airDragXZ in the wind-relative frame. So a
	// stationary airborne player in 15 m/s wind drifts toward 15 ×
	// windDragXZ m/s. Mirrors waterCurrentDrag for water currents.
	[Export] public float windDragXZ = 0.075f;
	[Export] public float jumpSpeed = 18f;
	[Export] public float jumpHoldGravityScale = 0.65f;
	// Baseline number of mid-air ("double") jumps. 0 = no air jump. Equipment and
	// status effects raise the live cap via the additive EStat.AirJumps modifier;
	// Player.AirJumpsMax composes both. Air jumps refill on landing.
	[Export] public int airJumpsMax = 0;

	[ExportGroup("Sliding & Skating")]
	// Steep-slope sliding & skating. A slide surface is any upward-facing
	// contact whose normal Y is in [slideSurfaceMinNormalY, cos(FloorMaxAngle)) —
	// steeper than walkable but not a vertical wall. While in contact the
	// player is "sliding" (puff FX, anim hook). Skating is the high-momentum
	// mode initiated by jumping and landing aligned with the downhill direction:
	// the runSpeed clamp is lifted, gravity accumulates along the slope tangent,
	// input acts as steering rather than a velocity replace.
	[Export] public float slideSurfaceMinNormalY = 0.2f;
	// Upper-bound normal-Y for the extended skate band — defines the
	// shallowest slope that both initiates and sustains skating. Slopes
	// steeper than this (n.Y < skateContinueMaxNormalY, i.e. angle > acos(this))
	// count as skate surfaces; shallower surfaces drop to normal grounded
	// movement. Strictly greater than cos(FloorMaxAngle) so the band spans
	// walkable ramps too. _sliding (puff FX) stays gated on the strict steep
	// band (n.Y < cos(FloorMaxAngle)); this field only controls _skating.
	// Default ≈ cos(30°): any slope ≥ 30° can launch a skate when alignment
	// is sharp, and a skate carries through ramp runouts down to 30°.
	[Export] public float skateContinueMaxNormalY = 0.866f;
	// Initiation gates for skating. Must land (not walk-into) on a slide
	// surface while inbound horizontal velocity is at least
	// skateInitiationMinSpeed and within an angle of the slope's downhill
	// direction whose cosine is at least skateInitiationAlignDot.
	[Export] public float skateInitiationMinSpeed = 5f;
	[Export] public float skateInitiationAlignDot = 0.5f;
	// Minimum inbound fall speed (m/s downward) at the landing tick required
	// to launch a skate. Walking off small ledges produces fall speeds well
	// below this; a deliberate jump or a real drop clears it easily. Compared
	// against -Velocity.Y captured just before MoveAndSlide.
	[Export] public float skateInitiationMinFallSpeed = 5f;
	// Hard cap on speed while skating — prevents runaway acceleration on
	// long slopes. Higher than sprintSpeed so skating is the fastest ground
	// mode by design.
	[Export] public float skateMaxSpeed = 18f;
	// Steering authority while skating. Per-second yaw rotation applied to
	// the velocity heading proportional to how off-axis the input is from
	// the current heading (0 = no steering, full input = full rate). Caps the
	// turn rate so skating feels heavy, not arcadey.
	[Export] public float skateSteerYawRate = 2.5f;
	// Tangent decel applied when input opposes velocity heading
	// (dot < -skateBrakeDotThreshold). m/s²; scales with align magnitude.
	[Export] public float skateBrakeDecel = 12f;
	[Export] public float skateBrakeDotThreshold = 0.5f;
	// Friction continually decelerating skate speed regardless of input.
	// Keeps a slope from acting as a permanent speed reservoir on the flat —
	// when the slope ends and gravity-along-slope drops to zero, this drains
	// momentum so the player returns to normal ground control.
	[Export] public float skateFriction = 8f;

	[ExportGroup("Wall Jump")]
	// Master gate for the wall jump. Off by default — the base character can't
	// kick off walls; a wall jump press while airborne just falls through to the
	// air-jump path (or nothing). Flip on (per-character data, or later a
	// gear/status grant) to enable the maneuver.
	[Export] public bool canWallJump = false;
	// While airborne, pressing Jump probes the capsule wallJumpCheckDistance
	// forward in the player's movement direction (or yaw when no input is
	// held). If the hit surface is steeper than the walkable floor angle and
	// not an overhang, velocity is replaced with (reflected_xz *
	// wallJumpSpeedXZ, wallJumpSpeedY, …). Gated on Velocity.Y >
	// -wallJumpMaxFallingSpeed so the player can't claw back a long fall.
	[Export] public float wallJumpSpeedY = 10f;
	[Export] public float wallJumpSpeedXZ = 8f;
	[Export] public float wallJumpCheckDistance = 0.5f;
	[Export] public float wallJumpMaxFallingSpeed = 15f;
	// Stamina paid on each successful wall jump. Mirrors dashStaminaCost:
	// gated on _stamina > 0 at press time (the press is rejected outright if
	// already exhausted), then deducted unconditionally — allowed to drive
	// stamina negative so chained wall jumps eat into the recharge runway.
	[Export] public float wallJumpStaminaCost = 15f;
	// Air-control blend window after a wall jump. During this many seconds the
	// airborne input-velocity rebuild lerps from the kick velocity (t=0) to the
	// input-driven velocity (t=1) instead of snapping each tick, so the arc
	// survives long enough to read as a kick rather than vanishing on the next
	// frame. Set to 0 to restore the snap-to-input behavior.
	[Export] public float wallJumpAirControlTime = 0.5f;

	[ExportGroup("Perception")]
	// Hard sightline cap (metres). Practical range is usually shorter — it
	// emerges from clarity vs perceptionMinimum, not from this.
	[Export] public float visionRange = 25f;
	// Exponent on the linear closeness term before it's multiplied by clarity.
	// <1 is CONCAVE: signal stays high across most of the range, so in clear
	// conditions targets cross perceptionInstant near max range and only the
	// outer band becomes a slow build. >1 would pull recognition in close.
	[Export] public float visionRangePower = 0.5f;
	// Pushes the closeness ramp's zero-crossing PAST visionRange: closeness =
	// 1 − d/(visionRange·this), still hard-culled at visionRange. So at the very
	// edge of range closeness is (1 − 1/this) instead of 0, letting a target hit
	// instant the moment it enters range in good light, instead of fading in at
	// the boundary. 1 = ramp reaches 0 right at the cap (old behaviour); 2 = edge
	// closeness 0.5. Higher flattens the curve (more of the range reads instant in
	// good light, but the floor discriminates range less in poor light).
	[Export(PropertyHint.Range, "1,4,0.05")] public float visionRangeCurveExtension = 2f;
	[Export] public float visibilityMovementMin = 0.5f;
	[Export] public float visibilityMovementPower = 2f;
	// Shapes how SOON darkness / fog / rain bite as each ramps in. Applied to the
	// condition's own strength (not the final multiplier), so each authored SimData
	// reduction still means exactly what it says at full strength — clarityPower
	// only bends the curve between clear and full. >1 = murkier sooner (a little
	// fog/dusk already cuts visibility); 1 = linear; clear lit air is unaffected
	// either way. Player→mob only; conspicuousness is excluded.
	[Export(PropertyHint.Range, "0.25,4,0.05")] public float clarityPower = 2f;
	// The perception floor: signal (closeness^power · clarity) must beat this to
	// register at all. Doubles as the practical range gate, the raycast perf
	// cull, and the cap on how slowly perception builds. Raise it to make poor
	// conditions cut range harder and bound stare time; lower it for a longer,
	// fainter tail. Set near 0 and every in-range target raycasts every tick.
	[Export] public float perceptionMinimum = 0.05f;
	// Signal at or above this snaps perception straight to Discovered (instant
	// recognition). In clear air clarity≈1, so this is roughly the closeness^power
	// needed to "instantly" spot a mob; lower = recognised from farther.
	[Export] public float perceptionInstant = 0.5f;
	[Export] public float perceptionRelaxationSpeed = 0.1f;
	// Seconds for the perception meter to fill (0→Discovered) at the two ends of
	// the partial-visibility band, with fill time fit to perceivability — NOT the
	// reverse. A target whose perceivability sits just above perceptionMinimum
	// fills in maxPerceptionFillSeconds (the slowest the meter ever moves); one
	// just below perceptionInstant fills in minPerceptionFillSeconds. Above
	// perceptionInstant is instant (0s); below perceptionMinimum never fills.
	// Keep min < max.
	[Export(PropertyHint.Range, "0.1,20,0.1")] public float minPerceptionFillSeconds = 2f;
	[Export(PropertyHint.Range, "0.1,20,0.1")] public float maxPerceptionFillSeconds = 6f;
	// Per-sense multipliers applied to the vision / hearing perception delta
	// before they're summed and accumulated. Mirrors MobData; the player's
	// perception of mobs (PlayerPerception.Tick) uses these.
	[Export] public float visionStrength = 1f;
	[Export] public float hearingStrength = 1f;
	// Hearing reach scalar. A sound of `decibels` is heard if
	// `decibels * hearingRange > distance`. The player's state transitions
	// (Hidden→Detected→Discovered) still require active visual contact —
	// hearing primes perception but can't cross the threshold alone.
	[Export] public float hearingRange = 10f;
	[Export] public float hearingRangePower = 0.5f;

	[ExportGroup("Eye Dilation")]
	// Dark-adaptation ("night eyes") — a sim-owned 0..1 state on Player.EyeDilation,
	// driven by the perceived light where the player stands (1 in pitch dark, 0 in
	// bright light). GameClient reads it to drive the eye_adaptation render global
	// (lit shaders lift shadows / blow highlights), and PlayerPerception folds it
	// into the darkness-suppression term so a dark-adapted player notices things in
	// the gloom slightly better.
	//
	// Seconds to dilate toward darkness — slow, like real pupils adjusting.
	[Export(PropertyHint.Range, "0.1,15,0.1")] public float eyeDilationDilateSeconds = 3.0f;
	// Seconds to constrict toward light — fast; bright light hits the eye at once.
	[Export(PropertyHint.Range, "0.05,5,0.05")] public float eyeDilationConstrictSeconds = 0.4f;
	// How much full dilation relieves the darkness perception penalty, in [0,1].
	// PARTIAL by design (0.35 = at full dilation the dark only costs 65% of normal).
	// Stacks with the NightVision equipment stat's relief. 0 = visuals only, no
	// gameplay visibility help.
	[Export(PropertyHint.Range, "0,1,0.01")] public float eyeDilationVisionRelief = 0.35f;

	[ExportGroup("Perceivability")]
	// Base conspicuousness of the player to a mob's VISION — the player-side
	// mirror of MobData.prominence. Multiplies the mob→player clarity alongside
	// the situational stealth terms (light / movement / camouflage), so it scales
	// how easily mobs pick the player up everywhere at once. 1 = neutral; lower
	// for an inherently sneakier build (small, crouched, dark gear). Does not
	// affect hearing or smell.
	[Export] public float prominence = 1f;
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

	[ExportGroup("Swimming")]
	// Minimum contiguous water-column depth (in voxels) at the player's feet
	// that triggers swimming — buoyancy, current drag, and the swimSpeed cap
	// kick in once the column reaches this depth; otherwise the player wades on
	// the bottom with ground physics. Mirrors MobData.swimDepthThreshold (and is
	// measured the same way), so a mob with the matching value swims and wades
	// at the exact same depths as the player. 2 = swim in 2+ deep water, wade
	// through 1-voxel puddles.
	[Export] public float swimDepthThreshold = 2f;
	[Export] public float shallowWaterSpeed = 0.5f;
	[Export] public float swimSpeed = 3.5f;
	[Export] public float swimVerticalSpeed = 4f;
	[Export] public float waterSinkSpeed = 2f;
	[Export] public float buoyancyAcceleration = 15f;
	[Export] public float waterDrag = 5f;
	// Horizontal drag, velocity-proportional. Per tick, XZ velocity decays
	// by `v * waterHorizontalDrag * dt` so a fast entry sheds momentum
	// quickly while a normal-speed swim barely feels it. Steady-state swim
	// speed under sustained input is roughly waterAcceleration /
	// waterHorizontalDrag — make sure that ratio stays above swimSpeed or
	// the player can't reach their swim target.
	[Export] public float waterHorizontalDrag = 3f;
	[Export] public float waterSurfaceOffset = 1f;
	[Export] public float waterJumpOffset = 1.5f;
	[Export] public float swimJumpSpeed = 8f;
	// Linear horizontal acceleration toward the input target while swimming
	// (m/s²). The "target" folds in waterCurrentDrag × local current so the
	// steady-state still matches the snap behavior (player at rest drifts at
	// current × drag; with input, drifts at input + current × drag), but
	// reaching it now takes ramp-in time so swimming feels weighted instead
	// of snapping. Set very high to approximate the old snap.
	[Export] public float waterAcceleration = 12f;
	// Fraction of the local water current's velocity added to the player's
	// horizontal velocity each tick while swimming. Input is applied first
	// each tick and this layers on top, so 1.0 means the player drifts at
	// exactly the current's m/s when standing still and swims across-the-
	// current at input+current. Values above 1 amplify the push (debris in
	// rapids); below 1 lets a strong swimmer fight the flow.
	[Export] public float waterCurrentDrag = 1f;

	[ExportGroup("Inventory")]
	// Must match the number of backpack ItemSlotPanels wired in
	// inventory_panel.tscn — every data slot has to be visible, or items can
	// land in an un-rendered slot and appear to vanish.
	[Export] public int backpackCapacity = 12;

	// The lantern every character spawns carrying, seeded into the dedicated
	// Lantern slot (never the Equipment hotbar). Shared across all party members
	// since PlayerData is the common base tuning. Null = characters spawn without
	// a lantern.
	[Export] public LanternData startingLantern;

	[ExportGroup("Combat")]
	[Export] public float maxHealth = 100f;

	// Fallback melee weapon used by the melee attack when the WeaponLeft slot is
	// empty — the player's bare-handed punch/kick. Authored as an ordinary
	// WeaponData (its own actionProfile, damage, animSet, no heldModel) so unarmed
	// combat shares the entire weapon timeline / event pipeline; it just lives on
	// the player instead of in the inventory. Player lazily wraps it in a
	// WeaponState the first time it's needed. Null = no unarmed attack (an empty
	// melee press does nothing).
	[Export] public WeaponData unarmedWeapon;

	// Inherent stat modifiers. Composed with equipped ArmorData.modifiers
	// and active StatusEffectData.modifiers when the actor queries any
	// stat (incoming-damage scale by tag, armor penetration / blunt / knockback
	// magnitudes by tag, move speed, sense multipliers, temperature
	// thresholds, etc.). 1.0 (or no entry) is neutral for multiplicative
	// stats; 0 is neutral for additive stats. Vulnerabilities author
	// multiplier > 1.
	[Export] public Godot.Collections.Array<StatModifier> modifiers;

	// Maximum angle (radians) between the mob's facing direction and the
	// player→mob vector at hit time for the attack to count as a backstab.
	// Backstab is purely positional — it fires regardless of whether the mob is
	// aware of the player; Mob.Hit folds OnBackstab modifiers onto the live hit
	// whenever the angle check passes. ~45° (Pi/4) is the default.
	[Export] public float backstabAngle = 0.785f;

	// Queued-attack input window: a completed tap (press AND release) that
	// lands while the pressed weapon can't yet fire (runner busy, or that
	// weapon cooling) is banked and auto-fired at readiness, provided the
	// release falls within this many seconds of the weapon becoming ready
	// (Player.WeaponReadyTimeMs). Player-wide input feel, deliberately NOT
	// per-weapon data — every weapon queues with the same responsiveness.
	[Export] public float weaponQueueWindowSeconds = 0.2f;

	// Cooldown (seconds) after the player STOPS sneak-blocking before the guard
	// can block or parry again — keyed off releasing the block, not off being
	// hit, so it stops flicker-blocking / instant re-crouch spam. Re-crouching
	// inside this window still assumes the pose but neither soaks nor parries
	// until it elapses. See Player.GetSneakBlockWeapon.
	[Export] public float blockReengageCooldown = 0.5f;

	[ExportGroup("Armor")]
	[Export] public float armorRechargeDelay = 3f;
	// Seconds for armor to refill from empty to full (the equipped MaxArmor).
	// The per-tick rate is derived as MaxArmor / armorRechargeTime, so the refill
	// takes this long regardless of how much armor is equipped. 0 = never recharges.
	[Export] public float armorRechargeTime = 3f;
	[Export] public float armorRecoverTime = 8f;

	[ExportGroup("Blood Regen")]
	// "Blood mana" drain regen, modeled on armor: every TryDrainBlood call
	// pushes _bloodRegenStartMs forward by bloodRegenDelay seconds, then
	// _drainedHealth refunds at bloodRegenSpeed HP/sec once the delay
	// elapses. A single shared delay (not per-drain) keeps the system
	// armor-simple — staggered drains during the delay all wait for the
	// same start time, after which the whole accumulated pool drains at
	// the flat rate.
	[Export] public float bloodRegenDelay = 3f;
	[Export] public float bloodRegenSpeed = 10f;

	[ExportGroup("Stamina")]
	// Stamina drains as the player performs effortful actions and refills on
	// its own. After any spend, recharge is gated for `staminaRechargeDelay`
	// seconds; once it begins, the bar refills from 0 to maxStamina over
	// `staminaRechargeTime` seconds (a flat rate, so partial spends refill
	// proportionally faster).
	[Export] public float maxStamina = 100f;
	[Export] public float staminaRechargeDelay = 1.5f;
	[Export] public float staminaRechargeTime = 3f;

	[ExportGroup("Dash")]
	// The motion itself (speed / duration / freeze-gravity) is authored on
	// the dashActionProfile's ApplyMotion event so weapons / mob lunges can
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
	// Underwater speed scalar applied to the dash event's motionForwardSpeed. 1.0
	// matches dry land; lower values give a slower swim-dash so the player
	// can't rocket through water.
	[Export] public float dashSwimSpeedScale = 0.5f;
	// Wall handling during dash. After MoveAndSlide the player iterates
	// collisions: if the dash direction is within dashWallHeadOnAngle of
	// the wall normal (radians), the dash short-circuits (head-on bonk).
	// Otherwise the dash direction is reprojected onto the wall plane so
	// the player slides along it at full speed.
	[Export] public float dashWallHeadOnAngle = 0.785f;

	[ExportGroup("Sprint")]
	// Sprint: continuous movement modifier engaged by holding Dash past the
	// initial dash burst. Sprint is intent-based (any hold + move input);
	// stamina gates the speed boost but not the intent — holding Dash with
	// depleted stamina drops to moveSpeed but still arms the recharge delay,
	// so the player can't refill while gripping the sprint button.
	[Export] public float sprintSpeed = 12f;
	[Export] public float sprintStaminaDrainPerSecond = 15f;
	// Sprint speed while swimming. Used by the dash-exit clamp when the
	// player ends a dash in water; the moving swim anim (SwimSprint) is
	// authored separately and selected by _sprinting alone.
	[Export] public float swimSprintSpeed = 6f;
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

	[ExportGroup("Mob Push")]
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

	[ExportGroup("Temperature")]
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

	[ExportGroup("Wetness")]
	// Player accumulates wetness in [0, 1] while exposed to rain or in
	// water; storage and arm / disarm hysteresis live on the Wet
	// StatusEffectData (EBuildupBehavior.ContinuousArm). Standing in water
	// snaps the meter to 1 immediately — you're soaked the moment you step
	// in.
	//
	// Times here are seconds to go 0→1 (soak) or 1→0 (dry) when the
	// corresponding input is at full strength. wetnessRainSoakSeconds = 50
	// means ~25 seconds in full RainIntensity = 1 rain to cross a 0.5
	// meter mark (and ~50 seconds to fully saturate). wetnessDrySeconds is
	// the *baseline* dry time at calm, neutral-humidity conditions — wind
	// and humidity scale the dry rate via the modifiers below.
	// wetnessWarmthDrySeconds is the campfire dry time, deliberately
	// unaffected by weather (radiant heat, not evaporation) and used as a
	// FLOOR on the effective rate inside a warmth zone — so a stiff
	// outdoor wind can still beat a fire but a humid still day never
	// drops you below it. Set to 0 to disable that source/sink.
	[Export(PropertyHint.Range, "0,600,1,or_greater")] public float wetnessRainSoakSeconds = 50f;
	[Export(PropertyHint.Range, "0,600,1,or_greater")] public float wetnessDrySeconds = 333.33f;
	[Export(PropertyHint.Range, "0,600,1,or_greater")] public float wetnessWarmthDrySeconds = 10f;
	// Rain shelter threshold against vertical sky exposure (WorldState
	// .GetSkyExposure01, in [0,1]: 1 = open sky, 0 = fully covered by a roof /
	// overhang / cave ceiling / dense canopy). At or below this exposure the
	// player is fully dry; soak ramps linearly to full at open sky. Mid-range
	// default: a thin canopy (high exposure) soaks you slowly while a moderate
	// canopy or any solid cover (exposure below the threshold) keeps you dry.
	// SkyExposure is the non-leaky vertical field, so a cave mouth's sideways
	// light leak never registers as rain exposure.
	[Export(PropertyHint.Range, "0.01,1,0.01")] public float rainShelterSkyThreshold = 0.5f;
	// A carried lantern is doused when the rain the player is actually exposed to
	// (RainIntensity, gated by the same shelter ramp as wetness) reaches this
	// strength — "heavy rain". Swimming douses unconditionally. Dousing is
	// one-way: the player must relight manually once dry / out of the water, it
	// never auto-relights when conditions ease. 1.0 disables rain dousing
	// (only a full downpour would ever hit it).
	[Export(PropertyHint.Range, "0.01,1,0.01")] public float lanternDouseRainThreshold = 0.6f;
	// Wind accelerates drying via evaporation. SampleWindSpeed already
	// zeroes out under overhead cover, so this only contributes outdoors.
	// Default 0.1 means the dry rate doubles at 10 m/s of wind and triples
	// at 20 m/s.
	[Export(PropertyHint.Range, "0,1,0.01,or_greater")] public float dryRateWindBoostPerMps = 0.1f;
	// Humidity slows drying. Default 0.7 means dry rate at humidity = 1
	// is 30% of baseline (≈3× the dry time); at humidity = 0 the baseline
	// is unmodified. Clamped so dry rate can't go negative — values > 1
	// just hold drying at zero at full humidity.
	[Export(PropertyHint.Range, "0,1,0.01")] public float dryRateHumidityDamping = 0.7f;
	// Air temperature relative to dryRateReferenceTempF scales the dry
	// rate linearly via dryRateTempBoostPerF (degrees F). With the
	// defaults (70°F reference, 2%/F), a 90°F afternoon dries you 40%
	// faster and freezing ambient (32°F) drops you to ~24% of baseline.
	// The multiplier is clamped at 0 so very cold air just stops drying
	// instead of going negative — useful when ambient drops below
	// ~20°F (rate would otherwise turn negative and grow wetness).
	[Export] public float dryRateReferenceTempF = 70f;
	[Export(PropertyHint.Range, "0,0.1,0.001,or_greater")] public float dryRateTempBoostPerF = 0.02f;
	// Per-second wetness contribution that flows from a fully-saturated piece
	// of equipped armor into the player's wetness meter (cascade armor → skin
	// through contact). Scaled linearly by each armor's current wetness — a
	// half-wet shirt contributes half this rate. Cascade is one-way today
	// (player → armor isn't modeled), so a soaked player wearing dry clothes
	// doesn't slowly wet them out, though that's a natural follow-up. Default
	// 0.02 gives roughly 50 seconds of full-wet armor contact to fully soak a
	// dry player from cascade alone, before factoring in the player's own
	// drying — in practice the steady-state is well below 1.
	[Export(PropertyHint.Range, "0,1,0.001,or_greater")] public float wetnessArmorCascadeRate = 0.02f;

	[ExportGroup("Dirtiness")]
	// Game-days of continuous WEAR for a piece of armor's grime meter to fill
	// 0→1 and read as "dirty". A worn dirty piece arms the player's Dirty
	// status effect, whose Scent modifier makes the player easier for mobs to
	// smell. One game day is SimData.DayLengthSeconds real seconds at
	// time_scale 1, and grime accrues on that same clock (CVars.timeScale
	// aware), so fast-forwarding the day/night cycle dirties armor faster too.
	// There is no passive decay — only getting the piece wet (rain or water)
	// scrubs it clean, and it need not be worn to be washed.
	[Export(PropertyHint.Range, "0.1,30,0.1,or_greater")] public float dirtyDaysToFull = 3f;

	[ExportGroup("Muddiness")]
	// Seconds of continuous walking on EGroundType.Mud ground to fill the
	// player's Muddy meter 0→1 and arm the effect (slower movement, masked
	// scent). The Muddy status' ContinuousArm armThreshold decides how full
	// the meter must get before the penalty kicks in, so the felt "time to get
	// muddy" is muddySoakSeconds × armThreshold.
	[Export(PropertyHint.Range, "0.5,120,0.5,or_greater")] public float muddySoakSeconds = 6f;
	// Seconds for the Muddy meter to drain 1→0 once the player is off mud and
	// out of water — mud flaking off as it dries. Slower than soaking so a
	// muddy stretch lingers after you leave it. Stepping into water ignores
	// this and rinses instantly.
	[Export(PropertyHint.Range, "0.5,600,0.5,or_greater")] public float muddyDrySeconds = 30f;

	[ExportGroup("Appearance")]
	// Palettes the character's modular look is picked from. PlayerState
	// stores the chosen INDEX into each (skinTone / hairColor / hairStyle), so a
	// spawn / character-creation choice is a small int and the menu of options
	// lives here as authored data. Colors feed the model's per-instance `recolor`
	// uniform (flat tone replace, see model_lit_body.gdshaderinc); the hair style
	// names a hair MeshInstance3D on the rig to show. Defaults give a usable
	// spread out of the box and are fully overridable in the inspector.
	//
	// Skin tones recolor the face + bare body meshes (see PlayerArmorVisual).
	[Export] public Color[] skinTones =
	{
		new Color(0.96f, 0.80f, 0.69f), // pale
		new Color(0.91f, 0.71f, 0.56f), // light
		new Color(0.80f, 0.58f, 0.42f), // tan
		new Color(0.55f, 0.38f, 0.26f), // brown
		new Color(0.36f, 0.24f, 0.17f), // deep
	};
	// Hair colors recolor whichever hair-style mesh is shown.
	[Export] public Color[] hairColors =
	{
		new Color(0.10f, 0.09f, 0.10f), // black
		new Color(0.26f, 0.17f, 0.10f), // dark brown
		new Color(0.45f, 0.30f, 0.17f), // brown
		new Color(0.85f, 0.68f, 0.39f), // blonde
		new Color(0.55f, 0.24f, 0.12f), // auburn
		new Color(0.62f, 0.62f, 0.64f), // grey
	};
	// NOTE: the hair-style *mesh* menu (which MeshInstance3D a hairStyle index
	// shows) is gender-specific and lives on the rig — see ModelAnimator
	// .hairStyleMeshNames, authored per package scene. Only the gender-agnostic
	// color palettes stay here.

	// Resolve a palette color by index, clamping out-of-range picks to the first
	// entry (and a hard-coded grey when the palette itself is empty) so a bad
	// authored index can never crash spawn — it just reads as a default look.
	public Color GetSkinTone(int index)
	{
		return PaletteColor(skinTones, index);
	}

	public Color GetHairColor(int index)
	{
		return PaletteColor(hairColors, index);
	}

	private static Color PaletteColor(Color[] palette, int index)
	{
		if (palette == null || palette.Length == 0)
		{
			return new Color(0.7f, 0.7f, 0.7f);
		}
		return palette[Mathf.Clamp(index, 0, palette.Length - 1)];
	}
}
