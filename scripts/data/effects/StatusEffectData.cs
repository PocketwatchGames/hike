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
}
