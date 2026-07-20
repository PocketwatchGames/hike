using Godot;

// Persistent state for a standalone worldgen safety zone (around a starting
// area). No per-instance data beyond position + scene — the footprint lives on
// the scene's CollisionShape3D and the zone is always active. Runtime node is a
// SafetyZone Area3D that marks overlapping players safe (see SafetyZone).
public class SafetyZoneSimState : EntitySimState
{
    public SafetyZoneSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return SafetyZone.Create(sim, this);
    }
}
