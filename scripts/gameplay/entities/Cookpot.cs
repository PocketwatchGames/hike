using System.Collections.Generic;
using Godot;

// A pot over its own fire. The first use lights the fire; using it while lit
// opens the CookpotScreen. Lighting it douses nothing, and it never goes out.
//
// The pot holds at most one meal (CookpotSimState.Meal), cooked in it from the
// CookingScreen. Anyone may eat from it any number of times; it empties only
// when the meal goes off, MealData.potHours after cooking. A new meal replaces
// the old one.
[GlobalClass]
public partial class Cookpot : Node3D, IInteractive, IWorldEntity
{
    // Shown while lit — the Cookpot entry.
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
    // The pot's contents: shown while a meal is in it, tinted with its potColor
    // through the model shader's `recolor` instance uniforms.
    [Export] private GeometryInstance3D _contents;
    // How often the pot looks at the clock to empty a meal that has gone off.
    // Presentation only — what is in the pot is always read against the clock.
    [Export(PropertyHint.Range, "0.5,30,0.5")] private float _mealCheckSeconds = 5f;

    const string RecolorParam = "recolor";
    const string RecolorAmountParam = "recolor_amount";
    const double HoursPerDay = 24d;

    private Sim _sim;
    private CookpotSimState _simState;
    private Fx _loopEffect;
    private readonly List<MeshInstance3D> _glowMeshes = new();
    private readonly ItemState[] _inputSlots = new ItemState[StationPanel.SlotCount];
    private float _mealCheckRemaining;

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;
    // Experimentation inputs the CookingScreen binds to.
    public ItemState[] InputSlots => _inputSlots;
    // Cooking here draws on the party stash and banks what is discovered at once.
    public bool IsCamp => true;

    // The meal in the pot now, or null.
    public MealData Meal => _simState?.MealAt(_sim?.WorldClockDays ?? 0d);

    // Cook `meal` into the pot, replacing whatever was in it.
    public void Fill(MealData meal)
    {
        if (_simState == null || meal == null || _sim == null)
        {
            return;
        }
        _simState.Meal = meal;
        _simState.MealGoneAtClock = _sim.WorldClockDays + meal.potHours / HoursPerDay;
        ApplyContents();
    }

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
        GameClient.Current?.OpenCookpotScreen(this);
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
        instance._sim = sim;
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
        instance.ApplyContents();
        return instance;
    }

    // Show the meal in the pot, or hide the contents when there is none. A meal
    // that has gone off is cleared from the sim state here, so saves don't carry it.
    private void ApplyContents()
    {
        MealData meal = Meal;
        if (meal == null && _simState != null)
        {
            _simState.Meal = null;
        }
        if (_contents == null)
        {
            return;
        }
        _contents.Visible = meal != null;
        if (meal != null)
        {
            _contents.SetInstanceShaderParameter(RecolorParam, meal.potColor);
            _contents.SetInstanceShaderParameter(RecolorAmountParam, 1f);
        }
        _mealCheckRemaining = _mealCheckSeconds;
    }

    public override void _Process(double delta)
    {
        if (_simState?.Meal == null)
        {
            return;
        }
        _mealCheckRemaining -= (float)delta;
        if (_mealCheckRemaining <= 0f)
        {
            ApplyContents();
        }
    }
}
