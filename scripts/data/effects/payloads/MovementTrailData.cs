using Godot;

// Hazard wake a status effect drops behind the carrying actor while it dashes or
// dashes (StatusEffectController.TickMovementTrail). Null on StatusEffectData.trail = none.
// [Tool] so the editor can bind it under its [Tool] parent StatusEffectData.
[Tool]
[GlobalClass]
public partial class MovementTrailData : Resource
{
	// Scene dropped at the actor's feet each interval. Author it self-expiring (a GasCloud
	// owning its own DamageZone + Fx, like flame_trail.tscn) — the controller just spawns it.
	[Export] public PackedScene zoneScene;

	// Seconds between drops (smaller = denser).
	[Export(PropertyHint.Range, "0.05,2,0.01,or_greater")] public float dropInterval = 0.2f;
}
