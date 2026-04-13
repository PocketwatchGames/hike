using Godot;

[GlobalClass]
public partial class WaterTrigger : Area3D
{
    public override void _Ready()
    {
        CollisionLayer = (uint)ECollisionLayer.Water;
        CollisionMask = (uint)(ECollisionLayer.Player | ECollisionLayer.Mob);
        BodyEntered += OnBodyEntered;
        BodyExited += OnBodyExited;
    }

    private void OnBodyEntered(Node3D body)
    {
        if (body is Player player)
        {
            player.WaterAreaEntered();
        }
    }

    private void OnBodyExited(Node3D body)
    {
        if (body is Player player)
        {
            player.WaterAreaExited();
        }
    }
}
