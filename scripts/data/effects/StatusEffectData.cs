using Godot;

// Authored data for a status effect held by a Player or Mob — icon, display
// name, and the per-tick stats the runtime applies while the effect is active.
// Designed to grow with additional stat fields (move-speed multiplier,
// perception multiplier, ...) by appending new [Export]s; existing .tres files
// keep loading because additions default to neutral values.
[GlobalClass]
public partial class StatusEffectData : Resource
{
	[Export] public Texture2D icon;
	[Export] public StringName displayName;

	// Per-second HP delta. Positive damages (poison), negative heals
	// (regeneration). Applied in 1-second chunks via the state's tick
	// accumulator so a 0.6 dps effect ticks as integer damage at the right
	// average rate rather than fractional damage every physics frame.
	[Export] public float damagePerSecond;

	// Default seconds the effect lasts once timed. 0 = situational; the
	// gameplay system that armed the effect (weather, water trigger, etc.)
	// owns the lifetime and arms a removal timer explicitly when one is
	// wanted (e.g. wet effect arms 10s only after the player leaves water).
	[Export] public float duration;

	// Per-footprint visual modulators. The actor's footprint emitter sums
	// these across all active StatusEffectStates and multiplies into the
	// per-ground FootprintData baseAlpha / durationSeconds at spawn time.
	// Defaults are 1 so existing effects don't change footprint behavior;
	// the Wet effect .tres bumps both above 1 so a wet actor leaves more
	// visible, longer-lasting prints.
	[Export] public float footprintAlphaMultiplier = 1f;
	[Export] public float footprintDurationMultiplier = 1f;

	// Per-tick motion modulators. The actor's movement update sums these
	// multiplicatively across every active StatusEffectState and applies the
	// product to its move speed (and to the sprite animator's playback rate
	// via LitSpriteAnimator.effectSpeedMultiplier) so movement and footwork
	// stay in lockstep. Defaults are 1 so existing effects don't change
	// motion; the Cold effect drops both to 0.75 to make a chilled actor
	// trudge with matching slowed animation. The two fields are independent
	// so authors can desync them on purpose (e.g. a "drunk" effect could
	// slow animation more than movement).
	[Export] public float movementMultiplier = 1f;
	[Export] public float animationSpeedMultiplier = 1f;

	// Per-effect multiplier applied to incoming healthDamage on the actor's
	// HurtBox hit. Multiplicative across active effects — StatusEffectController.
	// DamageMultiplier reads the product; Player.OnHurtBoxHit scales incoming
	// healthDamage by it and Player.GetHitType returns None when the product
	// is zero. 1.0 is neutral; 0.0 is full invulnerability (dash i-frames);
	// 0.5 is a damage-reduction buff; values >1 amplify damage (glass-cannon
	// debuff).
	[Export] public float damageMultiplier = 1f;

	// Per-effect shifts to the player's hot / cold trigger thresholds in
	// degrees F. Player.cs sums these across every active StatusEffectState
	// and applies them as: effective coldThreshold = base - sumColdResistance,
	// effective hotThreshold = base + sumHeatResistance. Positive resistance
	// shrinks the reachable danger band (harder to trigger); negative
	// resistance widens it (easier). Wet authors -25 cold / +25 heat — soaked
	// skin chills sooner and resists overheating.
	[Export] public float coldResistance;
	[Export] public float heatResistance;

	// Audiovisual cues bound to the effect's lifecycle. `startFx` and `endFx`
	// are one-shot Fx scenes spawned on the actor at apply / remove. `loopFx`
	// is a looping Fx scene (Fx._loop = true) parented to the actor while the
	// effect is active and Stop()'d when it's removed.
	[Export] public PackedScene startFx;
	[Export] public PackedScene endFx;
	[Export] public PackedScene loopFx;
}
