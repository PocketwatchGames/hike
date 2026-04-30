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
	void PlayAnim(StringName name);
}
