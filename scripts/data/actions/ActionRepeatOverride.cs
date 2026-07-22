using Godot;

// One swing within a repeating combo action (ItemAction.repeatActionOverrides).
// The array index IS the swing number and the array length IS the combo length:
// the driving WeaponState.repeatIndex advances one entry per press while the
// combo window (comboExpireMs) stays open, wrapping back to 0 after the final
// swing. Each entry layers per-swing tweaks over the base ItemAction — a
// default entry just replays the base swing, so only the swings that differ
// (typically the finisher) need non-default values.
//
// [Tool] so the [Tool] ItemAction can instantiate it as its real type in the
// editor (see CLAUDE.md sub-resource rule). Per-swing anim / release-fx
// overrides land here in the upcoming anim+fx pass; this pass wires only the
// two combat fields below.
[Tool]
[GlobalClass]
public partial class ActionRepeatOverride : Resource
{
	// Multiplies THIS swing's outgoing health damage, on top of the weapon's
	// other multipliers (level, status). 1 (default) = unchanged. The classic
	// use is a final-swing haymaker (2 on the last entry). Applied in
	// ItemEventHandlers.ResolveHit.
	[Export] public float damageMultiplier = 1f;

	// Recovery tail after THIS swing (time from Active end until the weapon can
	// fire again — same post-swing semantics as ItemAction.cooldownSeconds),
	// replacing the base value. < 0 (default) inherits the base — so a combo
	// finisher can carry a longer recovery than the quick lead-in swings while
	// the plain swings share one authored value.
	[Export] public float cooldownSeconds = -1f;

	// Anim slot for THIS swing: replaces the animName of every PlayAnim event
	// in the base tier's Active timeline (the per-weapon clip still resolves
	// through WeaponData.animSet, so e.g. Attack2 on a finisher plays that
	// weapon's heavy clip). None (default) keeps the base event's anim.
	[Export] public EAnimation animName = EAnimation.None;

	// Release fx for THIS swing: replaces the base tier's releaseEffect at
	// activation (the swing's swoosh/voice one-shot). Null (default) keeps the
	// base tier's.
	[Export] public PackedScene releaseEffect;
}
