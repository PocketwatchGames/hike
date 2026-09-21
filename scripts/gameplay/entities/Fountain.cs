using System.Collections.Generic;
using Godot;

// Anything the player drinks from: a healing or mana fountain, a well, a
// cauldron. What a drink DOES is the placement's list of ItemEffects (see
// FountainSpawnEntry); this node owns only when it can be used and what "ready"
// looks like.
//
// Ready = enabled (its enabledVariable, if any, is true) AND off cooldown (a
// RegrowDay deadline, so it survives streaming and save/load). The scene
// authors the ready look — nodes shown while ready (a fountain's water), and
// optionally a light, a lit/doused material swap and a loop Fx (a cauldron's
// fire). Both halves are event-driven: the day rollover and the script-variable
// bank, never a per-frame poll.
[GlobalClass]
public partial class Fountain : Node3D, IInteractive, IWorldEntity
{
    [Export] private Godot.Collections.Array<InteractiveAction> _actions = new();
    [Export] private Discoverable _discoverable;
    [Export] private Node3D _hudNode;
    [Export] private Node3D[] _readyNodes = System.Array.Empty<Node3D>();
    [Export] private StationaryLight _light;
    // Sub-model swapped between the lit and doused materials — point it at just
    // the part that glows (the logs), so the rest keeps its imported material.
    [Export] private Node3D _glowModel;
    [Export] private Material _litMaterial;
    [Export] private Material _dousedMaterial;
    [Export] private PackedScene _loopEffectScene;

    private FountainSimState _simState;
    private Sim _world;
    private Fx _loopEffect;
    private readonly List<MeshInstance3D> _glowMeshes = new();

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    public void OnSpawned(Sim sim) { }

    public override void _ExitTree()
    {
        if (_world == null)
        {
            return;
        }
        _world.OnNewDay -= HandleNewDay;
        ScriptVariableBank vars = _world.WorldState?.SimState?.ScriptVars;
        if (vars != null)
        {
            vars.OnChanged -= HandleVariableChanged;
        }
    }

    private bool IsEnabled()
    {
        StringName gate = _simState?.EnabledVariable;
        if (gate == null || gate.IsEmpty)
        {
            return true;
        }
        return _world?.WorldState?.SimState?.ScriptVars?.GetBool(gate) ?? false;
    }

    private bool IsOffCooldown()
    {
        return _simState == null || _simState.IsRegrown(Sim.Current?.DayNumber ?? 0);
    }

    private void HandleNewDay(int day)
    {
        ApplyReadyVisual(CanInteract(), fade: true);
    }

    private void HandleVariableChanged(StringName id)
    {
        if (_simState?.EnabledVariable != null && id == _simState.EnabledVariable)
        {
            ApplyReadyVisual(CanInteract(), fade: true);
        }
    }

    private void ApplyReadyVisual(bool ready, bool fade)
    {
        foreach (Node3D node in _readyNodes)
        {
            if (node != null)
            {
                node.Visible = ready;
            }
        }
        _light?.SetActive(ready, fade);
        Material mat = ready ? _litMaterial : _dousedMaterial;
        if (mat != null)
        {
            foreach (MeshInstance3D mesh in _glowMeshes)
            {
                mesh.SetSurfaceOverrideMaterial(0, mat);
            }
        }
        if (ready && _loopEffect == null && _loopEffectScene != null)
        {
            _loopEffect = Fx.Create(_loopEffectScene, this, Vector3.Zero);
        }
        else if (!ready && _loopEffect != null)
        {
            _loopEffect.Stop();
            _loopEffect = null;
        }
    }

    private void CollectGlowMeshes(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is MeshInstance3D mesh)
            {
                _glowMeshes.Add(mesh);
            }
            CollectGlowMeshes(child);
        }
    }

    public bool CanInteract()
    {
        return IsEnabled() && IsOffCooldown();
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract() && (_discoverable == null || _discoverable.IsDiscovered);
    }

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        if (!CanActorInteract(player))
        {
            return null;
        }
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    public void Complete(int actionIndex)
    {
        if (!CanInteract())
        {
            return;
        }
        Player player = GameClient.Current?.Player;
        if (player == null)
        {
            return;
        }
        var context = new ActionContext { verb = EActionVerb.Use, target = player, worldPosition = GlobalPosition };
        foreach (ItemEffect effect in _simState.Effects)
        {
            effect?.Apply(player, context);
        }
        if (_simState.CooldownDays > 0)
        {
            _simState.RegrowDay = (Sim.Current?.DayNumber ?? 0) + _simState.CooldownDays;
            ApplyReadyVisual(false, fade: true);
        }
    }

    public static Fountain Create(Sim sim, FountainSimState data)
    {
        var instance = data.Scene.Instantiate<Fountain>();
        data.SeatTransform(instance);
        instance._simState = data;
        instance._world = sim;
        var baseWorldPos = new Vector3I(
            Mathf.FloorToInt(data.WorldPosition.X),
            Mathf.FloorToInt(data.WorldPosition.Y),
            Mathf.FloorToInt(data.WorldPosition.Z)
        );
        instance._light?.Initialize(sim.WorldState, sim, baseWorldPos);
        sim.AddChild(instance);
        if (instance._glowModel != null)
        {
            instance.CollectGlowMeshes(instance._glowModel);
        }
        // Snap to the spawned state — a fountain streaming in shouldn't fade up.
        instance.ApplyReadyVisual(instance.CanInteract(), fade: false);
        sim.OnNewDay += instance.HandleNewDay;
        ScriptVariableBank vars = sim.WorldState?.SimState?.ScriptVars;
        if (vars != null)
        {
            vars.OnChanged += instance.HandleVariableChanged;
        }
        return instance;
    }
}
