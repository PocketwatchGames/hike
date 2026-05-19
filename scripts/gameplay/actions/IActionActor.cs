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
}
