using System.Collections.Generic;
using Godot;

// Area3D that marks any overlapping Player "safe" (Player.IsSafe) while they
// stand inside it. Authored two ways:
//   * As a child of a lit heat source (campfire), toggled active/inactive by
//     the source's lifecycle — see Campfire.SetLit. Mirrors WarmthZone.
//   * As a standalone worldgen entity around a starting area (SafetyZoneSimState
//     / SafetyZoneSpawnEntry), always active and independent of any campfire.
//
// While a player is marked safe, aggressive mobs break off their attack, stare
// (BehaviorLookAt), and wander away rather than engaging — see
// TargetSafeCondition and the safe-gated AggroAcquiredCondition.
//
// Tracks every overlapping Player regardless of the active flag so a zone that
// activates while the player is already standing inside still marks them, and a
// deactivated zone releases a player still inside without waiting for a walk-out.
[GlobalClass]
public partial class SafetyZone : Area3D
{
    private bool _active = true;
    private readonly List<Player> _overlapping = new();

    public override void _Ready()
    {
        CollisionLayer = 0;
        CollisionMask = (uint)ECollisionLayer.Player;
        BodyEntered += OnBodyEntered;
        BodyExited += OnBodyExited;
        if (_active)
        {
            RegisterNavAvoidance();
        }
    }

    public override void _ExitTree()
    {
        World.Current?.UnregisterSafeZone(this);
    }

    // Register this zone's footprint so hostile mobs route around it
    // (WalkabilityGrid.SafeZone + MobNavigator.AvoidsSafeZones). Footprint radius
    // is sniffed from the first child collision shape — the same disc the
    // player-overlap trigger uses — so the nav keep-out matches the safe area.
    private void RegisterNavAvoidance()
    {
        World world = World.Current;
        if (world == null)
        {
            return;
        }
        world.RegisterSafeZone(this, GlobalPosition, GetFootprintRadius());
    }

    private float GetFootprintRadius()
    {
        foreach (Node child in GetChildren())
        {
            if (child is CollisionShape3D cs && cs.Shape != null)
            {
                switch (cs.Shape)
                {
                    case SphereShape3D sphere: return sphere.Radius;
                    case CylinderShape3D cyl: return cyl.Radius;
                    case CapsuleShape3D capsule: return capsule.Radius;
                    case BoxShape3D box: return Mathf.Max(box.Size.X, box.Size.Z) * 0.5f;
                }
            }
        }
        return 0f;
    }

    public void SetActive(bool active)
    {
        if (_active == active)
        {
            return;
        }
        _active = active;
        if (_active)
        {
            RegisterNavAvoidance();
        }
        else
        {
            World.Current?.UnregisterSafeZone(this);
        }
        for (int i = 0; i < _overlapping.Count; i++)
        {
            Player p = _overlapping[i];
            if (p == null)
            {
                continue;
            }
            if (_active)
            {
                p.EnterSafetyZone(this);
            }
            else
            {
                p.ExitSafetyZone(this);
            }
        }
    }

    private void OnBodyEntered(Node3D body)
    {
        if (body is not Player p)
        {
            return;
        }
        _overlapping.Add(p);
        if (_active)
        {
            p.EnterSafetyZone(this);
        }
    }

    private void OnBodyExited(Node3D body)
    {
        if (body is not Player p)
        {
            return;
        }
        _overlapping.Remove(p);
        if (_active)
        {
            p.ExitSafetyZone(this);
        }
    }

    // Standalone worldgen entity path (SafetyZoneSimState.CreateEntity). The
    // scene's CollisionShape3D defines the zone's footprint; it spawns active.
    public static SafetyZone Create(World world, SafetyZoneSimState data)
    {
        var instance = data.Scene.Instantiate<SafetyZone>();
        instance.Position = data.WorldPosition;
        world.AddChild(instance);
        return instance;
    }
}
