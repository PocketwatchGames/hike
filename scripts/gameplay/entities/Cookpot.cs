using System.Collections.Generic;
using Godot;

// A pot over its own fire. The first use lights the fire; using it while lit
// opens the CookingScreen. Unlike a Campfire it is not a camp: lighting it
// douses nothing, it never goes out, and cooking here has no stash and banks
// nothing (ICookingStation.IsCamp).
[GlobalClass]
public partial class Cookpot : Node3D, IInteractive, IWorldEntity, ICookingStation
{
    // Shown while lit — the Cook entry.
    [Export] private Godot.Collections.Array<InteractiveAction> _litActions = new();
    // Shown while unlit — the Light entry.
    [Export] private Godot.Collections.Array<InteractiveAction> _unlitActions = new();
    [Export] private Node3D _hudNode;
    [Export] private StationaryLight _light;
    // Sub-model swapped between the lit and doused materials — point it at just
    // the part that glows (the logs), so the pot keeps its imported material.
    [Export] private Node3D _glowModel;
    [Export] private Material _litMaterial;
    [Export] private Material _dousedMaterial;
    [Export] private PackedScene _lightOnEffectScene;
    [Export] private PackedScene _lightLoopEffectScene;
    // Recipe scope — Cooking.TryMatch only considers recipes of this station.
    [Export] private ECampfireType _campfireType;

    private CookpotSimState _simState;
    private Fx _loopEffect;
    private readonly List<MeshInstance3D> _glowMeshes = new();
    private readonly ItemState[] _cookingSlots = new ItemState[CampfireSimState.CampfireSlotCount];

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;
    public ItemState[] CookingSlots => _cookingSlots;
    public ECampfireType CampfireType => _campfireType;
    public bool IsCamp => true;

    private bool IsLit => _simState != null && _simState.Lit;

    public void OnSpawned(Sim sim) { }

    public bool CanInteract()
    {
        return true;
    }

    public bool CanActorInteract(Player player)
    {
        return CanInteract();
    }

    public Godot.Collections.Array<InteractiveAction> GetActions(Player player)
    {
        Godot.Collections.Array<InteractiveAction> active = IsLit ? _litActions : _unlitActions;
        return active != null && active.Count > 0 ? active : null;
    }

    public void Complete(InteractiveAction action)
    {
        if (!IsLit)
        {
            Light();
            return;
        }
        GameClient.Current?.OpenCookingScreen(this);
    }

    private void Light()
    {
        if (_simState == null || _simState.Lit)
        {
            return;
        }
        _simState.Lit = true;
        if (_lightOnEffectScene != null)
        {
            Fx.Create(_lightOnEffectScene, GetParent(), Position);
        }
        ApplyLitVisual(fade: true);
    }

    private void ApplyLitVisual(bool fade)
    {
        bool lit = IsLit;
        _light?.SetActive(lit, fade);
        Material mat = lit ? _litMaterial : _dousedMaterial;
        if (mat != null)
        {
            foreach (MeshInstance3D mesh in _glowMeshes)
            {
                mesh.SetSurfaceOverrideMaterial(0, mat);
            }
        }
        if (lit && _loopEffect == null && _lightLoopEffectScene != null)
        {
            _loopEffect = Fx.Create(_lightLoopEffectScene, this, Vector3.Zero);
        }
        else if (!lit && _loopEffect != null)
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

    public static Cookpot Create(Sim sim, CookpotSimState data)
    {
        var instance = data.Scene.Instantiate<Cookpot>();
        data.SeatTransform(instance);
        instance._simState = data;
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
        // Snap to the spawned state — a pot streaming in lit shouldn't fade up.
        instance.ApplyLitVisual(fade: false);
        return instance;
    }
}
