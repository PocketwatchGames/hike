using Godot;
using System.Collections.Generic;

// The touch sense of a plant you walk THROUGH (flowers): the PorousBody
// counterpart for something with no movement collider to slide against. While
// a player is inside it, it raises Brushed every physics tick with their
// horizontal direction of travel and speed — the same signature PorousBody
// raises, so FoliageRustle treats both alike. Sits on no layer (nothing queries
// it) and watches the Player layer only; processes only while occupied.
[GlobalClass]
public partial class BrushArea : Area3D
{
    // (horizontal push direction, walker speed m/s, walker position)
    public event System.Action<Vector3, float, Vector3> Brushed;

    // Below this the walker is standing in the plant, not pushing through it,
    // and the plant springs back upright.
    private const float MinBrushSpeed = 0.05f;

    private readonly List<CharacterBody3D> _inside = new();

    public override void _Ready()
    {
        CollisionLayer = 0;
        CollisionMask = (uint)ECollisionLayer.Player;
        Monitorable = false;
        BodyEntered += OnBodyEntered;
        BodyExited += OnBodyExited;
        SetPhysicsProcess(false);
    }

    private void OnBodyEntered(Node3D body)
    {
        if (body is CharacterBody3D walker && !_inside.Contains(walker))
        {
            _inside.Add(walker);
            SetPhysicsProcess(true);
        }
    }

    private void OnBodyExited(Node3D body)
    {
        if (body is CharacterBody3D walker)
        {
            _inside.Remove(walker);
            SetPhysicsProcess(_inside.Count > 0);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        for (int i = _inside.Count - 1; i >= 0; i--)
        {
            CharacterBody3D walker = _inside[i];
            if (!IsInstanceValid(walker))
            {
                _inside.RemoveAt(i);
                continue;
            }
            Vector3 v = walker.Velocity;
            v.Y = 0f;
            float speed = v.Length();
            if (speed < MinBrushSpeed)
            {
                continue;
            }
            Brushed?.Invoke(v / speed, speed, walker.GlobalPosition);
        }
        if (_inside.Count == 0)
        {
            SetPhysicsProcess(false);
        }
    }
}
