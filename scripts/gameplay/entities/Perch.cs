using Godot;

// A landing spot a flying mob can rest on, authored as a Node3D marker placed
// on a prop at the branch / ledge where a bird should perch. The marker's world
// position is the landing point — drag it onto the spot with the standard 3D
// move gizmo. Make it a child of the prop so it rides the prop's transform.
[GlobalClass]
public partial class Perch : Node3D
{
    // Facing (yaw, radians) the landed bird adopts. Cosmetic.
    [Export] public float facingYaw;

    // The mob occupying or inbound to this perch, or null. Transient runtime
    // state, never serialized.
    public Node3D Occupant;

    public bool IsFree => Occupant == null || !IsInstanceValid(Occupant);

    // World position of the landing point — used for flee queries, claims, and
    // placing the perched bird.
    public Vector3 WorldPosition => GlobalPosition;

    public bool TryClaim(Node3D mob)
    {
        if (!IsFree && Occupant != mob)
        {
            return false;
        }
        Occupant = mob;
        return true;
    }

    public void Release(Node3D mob)
    {
        if (Occupant == mob)
        {
            Occupant = null;
        }
    }

    public override void _Ready()
    {
        World.Current?.Perches.Add(this);
    }

    public override void _ExitTree()
    {
        World.Current?.Perches.Remove(this);
    }
}
