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
}
