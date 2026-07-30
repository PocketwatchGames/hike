using System;
using System.Collections.Generic;
using Godot;

// Campfire / cooking station. Standalone from Torch — they share a similar
// lit/doused shape in the editor (animator, light, warmth/damage zones, on/off
// fx) but the cooking workflow is forge-specific.
//
// Only one campfire in the world is lit at a time: lighting one douses every
// other (DouseOtherCampfires). Campfires spawn unlit except the party's spawn
// campfire (CampfireSpawnEntry.startLit); there is no manual Douse — a fire
// goes out only when another is lit.
//
// Interact verb dispatch:
//   * EActionVerb.Camp on a lit forge → opens the CampScreen, whose Cook tab
//     binds to this forge and reads/writes CampfireSlots (experimentation
//     inputs). Cooking is instant — no job timer; eating a recipe applies its
//     status effect to the chosen character immediately (see CookingScreen).
//   * Light → lights the flame (and douses all others).
[GlobalClass]
public partial class Campfire : Node3D, IInteractive, IWorldEntity, IMobWard
{
    [Export] private LitSpriteAnimator _animator;
    // Model-based forges (the 3D campfire) swap the mesh material between a
    // glowing lit variant and a cold doused variant. _glowModel is the sub-model
    // that should light up (the logs) — point it at just that instance, not the
    // whole prop, so siblings like the stone ring keep their imported material
    // and never glow. Both materials are optional, so sprite-based forges leave
    // them unwired and skip the swap.
    [Export] private Node3D _glowModel;
    [Export] private Material _litMaterial;
    [Export] private Material _dousedMaterial;
    [Export] private StationaryLight _light;
    [Export] private Node3D _hudNode;
    [Export] private DamageZone _damageZone;
    [Export] private WarmthZone _warmthZone;
    // Small "safe" bubble around a lit fire: aggressive mobs break off and
    // wander away while the player stands in it (see SafetyZone). Toggled with
    // the flame — a doused fire offers no safety.
    [Export] private SafetyZone _safetyZone;
    // Actions shown while the forge is lit — the Camp entry (Cook / Sleep / Stash).
    [Export] private Godot.Collections.Array<InteractiveAction> _litActions = new();
    // Actions shown while the forge is unlit — typically a single Light entry.
    [Export] private Godot.Collections.Array<InteractiveAction> _unlitActions = new();
    [Export] private PackedScene _lightOnEffectScene;
    [Export] private PackedScene _lightOffEffectScene;
    [Export] private PackedScene _lightLoopEffectScene;
    // Recipe scope for this station — Cooking.TryMatch only considers
    // recipes whose campfireType matches.
    [Export] private ECampfireType _campfireType;
    // Health restored per in-world hour slept while resting at this fire
    // (fraction of max). The camp screen's Sleep tab reads it for every rest
    // duration — a future bed could set a higher rate than a plain campfire.
    [Export(PropertyHint.Range, "0,1,0.01")] private float _healFractionPerHour = 0.1f;
    public float HealFractionPerHour => _healFractionPerHour;
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    private bool _active = true;
    private CampfireSimState _simState;
    private Fx _loopEffect;

    public CampfireSimState SimState => _simState;
    public ItemState[] CampfireSlots => _simState?.CampfireSlots;
    public bool IsLit => _active;
    public ECampfireType CampfireType => _campfireType;

    // IMobWard: a campfire wards off mobs that fear it (slimes) so they never
    // block interacting here. Independent of lit state on purpose — the ward
    // exists so the player can LIGHT an unlit fire while fearers are around
    // (lighting it then drives them off via the safety zone); requiring it to be
    // lit already would reintroduce the chicken-and-egg it's meant to solve.
    public bool WardsOff(Mob mob) => mob?.mobData?.fearsCampfire == true;

    private static readonly StringName AnimOn = "on";
    private static readonly StringName AnimOff = "off";

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
        Godot.Collections.Array<InteractiveAction> active = _active ? _litActions : _unlitActions;
        return active != null && active.Count > 0 ? active : null;
    }

    public void Complete(int actionIndex)
    {
        Godot.Collections.Array<InteractiveAction> active = _active ? _litActions : _unlitActions;
        InteractiveAction action = active != null && actionIndex >= 0 && actionIndex < active.Count
            ? active[actionIndex]
            : null;
        if (action == null)
        {
            return;
        }
        if (action.verb == EActionVerb.Light || (action.verb == EActionVerb.Camp && _active))
        {
            // Lighting a campfire and re-camping at a lit one both enter camp: fade
            // to black, light the fire if needed, seat the party around it on the
            // camp screen, then fade back in — the fade hides the party gather /
            // camera reframe. Fall back to just lighting if there's no client.
            GameClient gc = GameClient.Current;
            if (gc != null)
            {
                gc.EnterCampWithFade(this);
            }
            else
            {
                SetLit(true);
            }
            return;
        }
        SetLit(!_active);
    }

    // Light the fire if it isn't already. Public entry for the camp-entry flow,
    // which lights the forge while the screen is black before opening camp.
    public void Light()
    {
        SetLit(true);
    }

    // Toggle helper. Updates visuals, zones, and fx — items stay in
    // CampfireSlots for the player to reclaim on relight. Lighting this fire
    // douses every other campfire so only one is ever lit at a time.
    private void SetLit(bool lit)
    {
        if (_active == lit)
        {
            return;
        }
        _active = lit;
        if (_simState != null)
        {
            _simState.Active = _active;
        }
        if (_active)
        {
            DouseOtherCampfires();
        }

        UpdateVisuals();
        _light.SetActive(_active);
        _damageZone?.SetActive(_active);
        _warmthZone?.SetActive(_active);
        _safetyZone?.SetActive(_active);

        PackedScene oneShot = _active ? _lightOnEffectScene : _lightOffEffectScene;
        if (oneShot != null)
        {
            Fx.Create(oneShot, GetParent(), Position);
        }
        UpdateLoopEffect();
    }

    // Extinguish the world's one other lit campfire so exactly one burns at a
    // time. Only SimState.LitCampfire can be lit, so there's just the one
    // to douse — no scan. Its runtime node douses in full (visuals/light/zones)
    // when the chunk is loaded; when it's unloaded we clear the sim state's
    // Active bit so it stays dark the next time it streams in.
    private void DouseOtherCampfires()
    {
        SimState worldSim = Sim.Current?.WorldState?.SimState;
        if (worldSim == null)
        {
            return;
        }
        CampfireSimState prev = worldSim.LitCampfire;
        if (prev != null && prev != _simState)
        {
            if (prev.RuntimeNode is Campfire prevFire && GodotObject.IsInstanceValid(prevFire))
            {
                prevFire.SetLit(false);
            }
            else
            {
                prev.Active = false;
            }
        }
        worldSim.LitCampfire = _simState;
    }

    private void UpdateLoopEffect()
    {
        if (_active && _loopEffect == null && _lightLoopEffectScene != null)
        {
            _loopEffect = Fx.Create(_lightLoopEffectScene, this, Vector3.Zero);
        }
        else if (!_active && _loopEffect != null)
        {
            _loopEffect.Stop();
            _loopEffect = null;
        }
    }

    private readonly List<MeshInstance3D> _modelMeshes = new();
    private bool _modelMeshesCollected;

    private void UpdateVisuals()
    {
        // Sprite-based forges swap on/off frames here; model-based forges swap
        // the mesh material (lit glow vs cold ash). Both paths are optional —
        // whichever is wired runs, the other no-ops.
        _animator?.Play(_active ? AnimOn : AnimOff);
        ApplyModelMaterial();
    }

    // Swap the surface override material on every mesh under _glowModel to match
    // the lit/doused state. The lit material's emission makes the logs read as
    // burning even where the fire light doesn't reach; the doused one drops
    // emission and cools the tint. Meshes are collected once and cached.
    private void ApplyModelMaterial()
    {
        if (_glowModel == null || _litMaterial == null || _dousedMaterial == null)
        {
            return;
        }
        if (!_modelMeshesCollected)
        {
            CollectModelMeshes(_glowModel);
            _modelMeshesCollected = true;
        }
        Material mat = _active ? _litMaterial : _dousedMaterial;
        foreach (MeshInstance3D mesh in _modelMeshes)
        {
            if (mesh != null)
            {
                mesh.SetSurfaceOverrideMaterial(0, mat);
            }
        }
    }

    // Gather every MeshInstance3D under the model container (meshes live inside
    // instanced FBX sub-scenes).
    private void CollectModelMeshes(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is MeshInstance3D mesh)
            {
                _modelMeshes.Add(mesh);
            }
            CollectModelMeshes(child);
        }
    }

    public static Campfire Create(Sim sim, CampfireSimState data)
    {
        var instance = data.Scene.Instantiate<Campfire>();
        data.SeatTransform(instance);
        instance._simState = data;
        var baseWorldPos = new Vector3I(
            Mathf.FloorToInt(data.WorldPosition.X),
            Mathf.FloorToInt(data.WorldPosition.Y),
            Mathf.FloorToInt(data.WorldPosition.Z)
        );
        instance._light.Initialize(sim.WorldState, sim, baseWorldPos);
        sim.AddChild(instance);

        instance._active = data.Active;
        // A campfire that streams in lit is the world's one active fire — cache
        // it so a later light elsewhere can douse it even if this chunk has
        // since unloaded (see DouseOtherCampfires).
        if (instance._active)
        {
            SimState worldSim = sim.WorldState?.SimState;
            if (worldSim != null)
            {
                worldSim.LitCampfire = data;
            }
        }
        instance.UpdateVisuals();
        // Snap to the spawned state — a streaming-in forge shouldn't fade up.
        instance._light.SetActive(instance._active, fade: false);
        instance._damageZone?.SetActive(instance._active);
        instance._warmthZone?.SetActive(instance._active);
        instance._safetyZone?.SetActive(instance._active);
        instance.UpdateLoopEffect();

        return instance;
    }
}
