using Godot;

// A pull-lever that remote-triggers every loaded Trapdoor sharing its target
// link tag. The codebase has no entity-to-entity linkage otherwise
// (TriggerSource only reaches same-scene NodePath targets), so the link is a
// shared string resolved through Sim.GetEntities at pull time.
//
// Scope: only trapdoors whose chunk is currently loaded respond — a lever and
// its trapdoor are expected to sit in the same room / load radius. A pull with
// no loaded match is a no-op (the handle still throws).
[GlobalClass]
public partial class Lever : Node3D, IInteractive, IWorldEntity
{
    // Pivots on pull. The handle swings about its local X between the two
    // detents; purely cosmetic.
    [Export] private Node3D _handle;
    [Export(PropertyHint.Range, "0,120,1")] private float _throwAngleDeg = 55f;
    [Export] private float _throwSeconds = 0.2f;
    [Export] private Node3D _hudNode;
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    private LeverSimState _simState;
    private Sim _world;
    private bool _thrown;

    public void OnSpawned(Sim sim)
    {
        _world = sim;
    }

    public bool CanInteract()
    {
        return true;
    }

    public bool CanActorInteract(Player player)
    {
        return true;
    }

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    public void Complete(int actionIndex)
    {
        _thrown = !_thrown;
        if (_simState != null)
        {
            _simState.On = _thrown;
        }
        UpdateHandle(animate: true);
        FireLinked();
    }

    private void FireLinked()
    {
        string target = _simState?.TargetLinkTag ?? "";
        if (_world == null || string.IsNullOrEmpty(target))
        {
            return;
        }
        foreach (Trapdoor trapdoor in _world.GetEntities<Trapdoor>())
        {
            if (trapdoor.LinkTag == target)
            {
                trapdoor.Trigger(this);
            }
        }
    }

    private void UpdateHandle(bool animate)
    {
        if (_handle == null)
        {
            return;
        }
        float target = Mathf.DegToRad(_thrown ? _throwAngleDeg : -_throwAngleDeg);
        if (animate)
        {
            CreateTween().TweenProperty(_handle, "rotation:x", target, _throwSeconds)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }
        else
        {
            Vector3 r = _handle.Rotation;
            r.X = target;
            _handle.Rotation = r;
        }
    }

    public static Lever Create(Sim sim, LeverSimState data)
    {
        var instance = data.Scene.Instantiate<Lever>();
        data.SeatTransform(instance);
        instance._simState = data;
        instance._world = sim;
        instance._thrown = data.On;
        sim.AddChild(instance);
        instance.UpdateHandle(animate: false);
        return instance;
    }
}
