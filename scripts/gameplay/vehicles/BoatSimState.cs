using Godot;

// Persistent state for a placed Boat. Like other static-placement entities the
// boat carries only its spawn transform — the live momentum / float state is
// transient and rebuilt from the water column on spawn. Movers normally sync
// their runtime position back into WorldPosition before unload; a boat could do
// the same once it can drift while unridden across chunk boundaries, but for
// now it respawns at its authored anchor.
public class BoatSimState : EntitySimState
{
    public readonly float RotationY;

    public BoatSimState(Vector3 worldPosition, float rotationY, PackedScene scene)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return Boat.Create(sim, this);
    }
}
