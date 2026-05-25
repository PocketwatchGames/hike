using Godot;

// What the ActionRunner needs from its owner. Player and (phase 6) Mob both
// implement this. The runner is otherwise actor-agnostic: it walks the
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
	// and end-of-motion behavior. Actors that don't drive motion from authored
	// events (basic mobs today) may no-op.
	void ApplyMotion(float speed, float duration, bool freezeGravity);

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

	// Physical-state queries read by ActorStateRequirement. Players forward
	// to the live walk/swim state; mobs return sane defaults (grounded, dry)
	// until mob locomotion grows the equivalent state machines.
	bool IsGrounded { get; }
	bool IsSwimming { get; }

	// Product of every active status effect's outgoingDamageMultiplier. Used
	// by ResolveHit to scale the constructed HitInfo's healthDamage when this
	// actor sources a hit (battle-cry buffs, etc.). 1.0 = neutral.
	float OutgoingDamageMultiplier { get; }

	// Faction tag used by direct-hit handlers (Melee / Hitscan / Projectile)
	// to skip same-team hurtboxes when ItemEvent.friendlyFire is false. Player
	// returns ETeam.Player; mobs forward MobData.team.
	ETeam ActorTeam { get; }
}
