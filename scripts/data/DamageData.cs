using Godot;

// Authored template for a hit. Senders construct a runtime HitInfo from
// this resource (plus their own source / hit direction) before calling
// HurtBox.Hit. Same template can be referenced from a weapon, an event,
// a trap, or a damage zone.
[GlobalClass]
public partial class DamageData : Resource
{
	[Export] public float healthDamage = 0f;

	// Seconds of stun applied to the receiver. 0 = no stun. Plumbed through
	// HitInfo for receivers that implement stun; ignored otherwise.
	[Export] public float stun = 0f;

	// Status effects to append to the receiver on hit (poison, slow, burn,
	// etc.). Each entry is added independently — receivers append a fresh
	// StatusEffectState per entry, mirroring AddStatusEffect's behavior.
	[Export] public Godot.Collections.Array<StatusEffectData> statusEffects;
}
