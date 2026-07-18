using Godot;

// What the ActionRunner needs from its owner. Player and Mob both implement
// this. The runner is otherwise actor-agnostic: it walks the
// timeline, fires events, and asks the actor to resolve actor-specific bits
// (position for queries, animation playback, hurt-box exclusion for raycasts).
public interface IActionActor
{
	Vector3 ActorWorldPosition { get; }
	Vector3 ActorForward { get; }
	ulong GameTimeMs { get; }
	uint AttackHurtboxMask { get; }
	Rid? SelfHurtBoxRid { get; }
	Node3D AttackerNode { get; }
	void PlayAnim(EAnimation anim);

	// Kick off a motion phase. The runner fires this from an ApplyMotion event;
	// the actor's physics layer owns direction resolution, terrain interaction,
	// and end-of-motion behavior. `direction` selects whether the base axis is
	// the actor's facing or its move input; forwardSpeed is signed along that
	// resolved axis — negative drives the actor backward (hop-back / recoil).
	// Actors that don't drive motion from authored events (basic mobs today)
	// may no-op.
	void ApplyMotion(float forwardSpeed, float duration, bool freezeGravity, EMotionDirection direction);

	// Stamina gate for ItemAction.staminaCost. HasStamina is a non-mutating
	// peek used at press time to refuse an action the actor can't afford.
	// ConsumeStamina is an unconditional spend at EnterActive; actors are
	// expected to allow negative stamina (matching sprint/swim drain).
	bool HasStamina(float amount);
	void ConsumeStamina(float amount);

	// Blood-mana gate for ItemAction.bloodCost. HasBlood is a non-mutating
	// peek used at press time to refuse a drain that would kill the actor.
	// DrainBlood is an unconditional spend at EnterActive that subtracts
	// from current HP and arms the per-actor blood-regen delay. Mobs no-op
	// both (no blood-mana system today).
	bool HasBlood(float amount);
	void DrainBlood(float amount);

	// Reagent-cost gate for InteractiveAction.reagents, paid from the actor's
	// material pool. HasReagents is a non-mutating peek at press time (refuse an
	// interaction the actor can't afford the ingredients for); SpendReagents deducts
	// one cost's worth at natural completion and returns whether it spent. The player
	// draws from its backpack + the party stash — the same pool alchemy spells cast
	// from; mobs no-op (they never run reagent-costed interactives).
	bool HasReagents(System.Collections.Generic.IReadOnlyList<RecipeInput> reagents);
	bool SpendReagents(System.Collections.Generic.IReadOnlyList<RecipeInput> reagents);

	// Physical-state queries read by ActorStateRequirement. Players forward
	// to the live walk/swim state; mobs return sane defaults (grounded, dry)
	// until mob locomotion grows the equivalent state machines.
	bool IsGrounded { get; }
	bool IsSwimming { get; }

	// True while any active status effect is dealing damage-over-time. Read by
	// NoDamagingEffectRequirement to refuse rest actions (sleeping in a tent)
	// while bleeding/poisoned/burning. Both actors forward to their shared
	// StatusEffectController.
	bool HasDamagingStatusEffect { get; }

	// Product of every active status effect's outgoingDamageMultiplier. Used
	// by ResolveHit to scale the constructed HitInfo's healthDamage when this
	// actor sources a hit (battle-cry buffs, etc.). 1.0 = neutral.
	float OutgoingDamageMultiplier { get; }

	// Melee-only outgoing-damage scale. ResolveHit applies it on top of
	// OutgoingDamageMultiplier for melee-tagged swings only (ranged / thrown
	// are unaffected). The player folds in its PlayerState.strength; mobs
	// return 1.0 (no strength stat). 1.0 = neutral.
	float MeleeDamageMultiplier { get; }

	// Attacker-side per-level power scale for an attack fired from `slot`, applied
	// to BOTH the hit's healthDamage and every buildup it delivers. The player maps
	// the slot to its Melee/Ranged forge-upgrade level; a mob ignores the slot and
	// uses its difficulty Level. Both resolve through SimData.LevelOutgoingScale so
	// player and mob share one curve. 1.0 = neutral (unleveled / no upgrade). Melee
	// & hitscan apply it in ResolveHit; projectiles thread it through Launch.
	float OutgoingLevelScale(EInventorySlot slot);

	// Faction tag used by direct-hit handlers (Melee / Hitscan / Projectile)
	// to skip same-team hurtboxes when DamageData.friendlyFire is false. Player
	// returns ETeam.Player; mobs forward MobData.team.
	ETeam ActorTeam { get; }

	// Restore `amount` HP to this actor, clamped at MaxHealth. Used by the
	// vampiric (lifesteal) weapon mod to leech a fraction of the health damage a
	// landed attack deals back to the attacker. Symmetric with DrainBlood.
	void Heal(float amount);

	// Refill `amount` stamina points on this actor, clamped at MaxStamina. Used by
	// the stamina-on-hit weapon mod to top up the attacker on each landed hit.
	// Player-only; mobs have no stamina pool and no-op. Symmetric with Heal.
	void RestoreStamina(float amount);

	// Fire any status-effect-driven on-attack-impact payloads at `position`.
	// Called by the Melee / Hitscan handlers the moment an attack resolves its
	// impact point — an elite's lightning aura, a player's shock-enchant, etc.,
	// each authored as a StatusEffectData carrying an AoE burst. Player and Mob
	// forward to their shared StatusEffectController, so the same content works
	// on either actor. Symmetric with OutgoingDamageMultiplier: both let an
	// active status effect reshape the swing without the handler knowing which
	// effect (if any) is responsible.
	void TriggerAttackImpact(Vector3 position);

	// On-attack projectile mods this actor carries as BODY status effects (a
	// Fairy boon's homing missiles), independent of the wielded weapon. Player
	// and Mob forward to their shared StatusEffectController; the Melee / Hitscan /
	// Projectile handlers dispatch each mod's onAttackEvent through DoProjectile
	// with the mod's intrinsic damage. `trigger` selects OnSwing vs OnHit; `slot`
	// is the equipped weapon slot the firing attack came from, so a melee-slot
	// boon and a ranged-slot boon can be told apart. Null when none.
	Godot.Collections.Array<WeaponModData> BodyOnAttackMods(EWeaponModAttackTrigger trigger, EInventorySlot slot);
}
