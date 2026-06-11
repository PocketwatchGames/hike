using Godot;

// Authored template for a hit. Senders construct a runtime HitInfo from
// this resource (plus their own source / hit direction) before calling
// HurtBox.Hit. Same template can be referenced from a weapon, an event,
// a trap, or a damage zone.
//
// Conditional variants (crit vs base, dizzy-amplified knockback, etc.) are
// expressed as entries in `modifiers` — see DamageDataModifier. Receivers
// fold matching modifiers onto the live HitInfo via HitInfo.ApplyTrigger
// when the corresponding condition is detected.
[Tool]
[GlobalClass]
public partial class DamageData : Resource
{
	// Type tags carried by this hit (Fire, Melee, Magical, …). Receivers
	// fold their per-tag StatModifier entries (inherent + armor + active
	// status effects) against this mask at multiple sites: healthDamage
	// scale (any damage tag), armor-penetration-chance scale (EStat.ArmorPenetration only),
	// armor-chip scale (EStat.Blunt only), knockback magnitude (EStat.
	// Knockback only). Default None means the hit is untyped — no modifier
	// entry matches, so it lands at full strength. Author broadly: a basic
	// sword swing is Damage|Melee|Blunt, a fireball is Damage|Fire|Magical|
	// Ranged.
	private EStat _tags;
	[Export, CompactFlags] public EStat tags
	{
		get => _tags;
		set
		{
			if (_tags == value) { return; }
			_tags = value;
			EmitChanged();
		}
	}

	[Export] public float healthDamage = 0f;

	// Chance (0..1) that the entire hit bypasses the receiver's armor pool
	// and lands directly on health. 0 = always absorbed by armor (the legacy
	// behavior); 1 = always bypasses. Rolled once when the HitInfo is built
	// (HitInfo.armorPenetrationRoll) so the prediction in HurtBox.QueryHitType and the
	// real apply in HurtBox.Hit always agree on whether this swing penetrated armor.
	[Export(PropertyHint.Range, "0,1,0.01")] public float armorPenetration = 0f;

	// Multiplier on the healthDamage chip dealt to the receiver's armor pool —
	// final armor chip is `healthDamage * (1 + blunt)`, clamped to remaining
	// armor. 0 = baseline (chip == healthDamage); 1 = doubles the chip. Has
	// no effect on the damage that bleeds through on an armor-penetrating hit.
	[Export] public float blunt = 0f;

	// Seconds of hitstun applied to the receiver — short reaction lockout
	// that triggers the hitstun anim. 0 = no hitstun. Independent of dizzy:
	// dizzy is a heavy state crossed via a buildup meter; hitstun fires on
	// every hit that authors one and is the per-hit flinch.
	[Export] public float hitstun = 0f;

	// Magnitude of the horizontal knockback impulse, in m/s of velocity
	// change. Combined at apply time with HitInfo.hitDirection (set by the
	// sender) to form the actual impulse vector — receivers do
	// hitDirection.Normalized() * knockbackDistance and strip Y. 0 = no
	// knockback.
	[Export] public float knockbackDistance = 0f;

	// Seconds the receiver remains in the knockback state. Receivers may
	// use this to suppress input / hold the hitstun anim past the raw
	// impulse. 0 = apply impulse but no lockout window.
	[Export] public float knockbackTime = 0f;

	// Status effects to append to the receiver on hit (poison, slow, burn,
	// etc.). Each entry is added independently — receivers append a fresh
	// StatusEffectState per entry, mirroring AddStatusEffect's behavior.
	[Export] public Godot.Collections.Array<StatusEffectData> statusEffects;

	// Buildup contributions added to the receiver's per-effect meters on hit.
	// Each entry funnels `amount` into the meter for `effect`; crossing 1
	// applies the effect (and folds any modifier authored against the effect's
	// applyTrigger). Decay and clear-on-apply behavior live on the
	// StatusEffectData itself, so the same buildup contribution behaves
	// consistently across every DamageData that feeds the same effect.
	// Replaces the legacy `stun` field — dizzy is now a normal status effect
	// fed by buildup like any other.
	[Export] public Godot.Collections.Array<StatusEffectBuildup> buildups;

	// Conditional partial-override layers. Each modifier carries a trigger
	// (OnCrit, OnDizzy, …) and a flag mask selecting which fields it touches;
	// the receiver folds matching modifiers onto the live HitInfo at apply
	// time. Replaces the old `critDamageData` / `knockbackDistanceOnStun`
	// fields with a single extensible list.
	[Export] public Godot.Collections.Array<DamageDataModifier> modifiers;

	// Marks this hit template as a per-frame damage tick (DamageZone with a
	// fast tickInterval, etc.). Receivers route DoT hits into a per-second
	// HUD accumulator so a burn or poison cloud emits one rolled-up floating
	// number per second instead of one per physics frame. No effect on the
	// underlying damage application — only HUD rollup.
	[Export] public bool dot = false;

	// When false (default), direct-hit senders (Melee / Hitscan / Projectile
	// handlers, status-effect AoE bursts) skip hurtboxes whose owner is allied
	// with the attacker (ItemEventHandlers.CanDamage / Teams.AreAllied) — a
	// goblin's swing can't hurt its kin, the player can't strike a friendly NPC
	// or tamed companion. Author true for damage meant to spill onto everyone
	// regardless of team (a wild cleave, a friendly-fire fireball). The policy
	// rides on the hit payload so every sender that builds a HitInfo from this
	// template inherits it consistently. DamageZone hazards apply the same
	// CanDamage rule, but carry their own zone-level attackerTeam / friendlyFire
	// fields rather than reading this one.
	[Export] public bool friendlyFire = false;
}
