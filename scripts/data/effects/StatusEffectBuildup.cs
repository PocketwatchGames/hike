using Godot;

// How a contribution reaches the receiver's body, which decides whether a hit
// that connected actually delivers it. Written into .tres as an int: APPEND-ONLY.
//   Contact — lands whenever the hit connects (a blow's Dizzy, Shocked, Burning,
//             a breathed poison cloud).
//   Wound   — lands only if the hit took off health; one soaked entirely by
//             armor or a shield delivers nothing (a blade's Bleeding, a bite's venom).
public enum EBuildupDelivery
{
	Contact = 0,
	Wound = 1,
}

// Per-hit (or per-tick) contribution that lands a status effect on the receiver,
// in one of two modes (see applyImmediately). The single authored channel for
// every on-hit effect — DamageData.buildups, DamageDataModifier.addBuildups,
// WeaponModData.onHitBuildups all hold these; there is no separate direct
// status-effect list.
//
// [Tool] because DamageData / WeaponModData (both [Tool]) export lists of these —
// without it the editor blanks and then drops every authored buildup.
[Tool]
[GlobalClass]
public partial class StatusEffectBuildup : Resource
{
	// Status effect this contribution lands. Same instance may be referenced by
	// many templates — the meter lives on the receiver, keyed by this reference.
	[Export] public StatusEffectData effect;

	// When true, `effect` is applied directly on every hit (a Flaming weapon's
	// Burning, a Venomous weapon's Poison) — `amount` and the buildup meter are
	// ignored. When false (default), this feeds the meter by `amount` and the
	// effect lands only when the meter crosses 1 (Dizzy). Immediate apply still
	// runs the effect's removesOnApply / maxStack via the shared Add path, but
	// does NOT fire the effect's applyTrigger (only a meter cross does).
	[Export] public bool applyImmediately = false;

	// Buildup units added to the receiver's meter on hit (ignored when
	// applyImmediately). Crossing 1 applies `effect` once and (per
	// StatusEffectData.clearBuildupOnApply) either zeros the meter or subtracts 1
	// for the next apply. Per-tick paths (continuous damage zones, dot intervals)
	// scale by delta before adding.
	[Export] public float amount = 0f;

	[Export] public EBuildupDelivery delivery = EBuildupDelivery.Contact;
}
