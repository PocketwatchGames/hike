using System;
using System.Collections.Generic;
using Godot;

// Campfire / cooking station. Standalone from Torch — they share a similar
// lit/doused shape in the editor (animator, light, warmth/damage zones, on/off
// fx) but the cooking workflow is forge-specific.
//
// Interact verb dispatch:
//   * EActionVerb.Cook on a lit forge → opens the CookingScreen against
//     this forge. The screen drives StartForgeJob / CancelForgeJob via the
//     Cook button and reads ForgeSlots / ActiveForgeJob each frame.
//   * Anything else (or Cook on an unlit forge) → toggles the flame.
//
// Cook-job lifecycle:
//   * StartForgeJob seeds the timer; items stay in ForgeSlots so a Cancel
//     leaves the inputs intact.
//   * _PhysicsProcess decrements remainingSeconds; on expiry,
//     CompleteForgeJob drains the slots and routes the output either
//     through deliveryCallback (set by the bound CookingScreen) or
//     spawns Loot at the forge for the player to find later.
//   * Dousing the flame (SetLit(false)) cancels any active job — items
//     stay in slots for the player to reclaim by relighting.
[GlobalClass]
public partial class Forge : Node3D, IInteractive, IWorldEntity
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
    // Actions shown while the forge is lit. The first entry is the default
    // (Cook); secondary entries (Douse) surface through the hold-menu.
    [Export] private Godot.Collections.Array<InteractiveAction> _litActions = new();
    // Actions shown while the forge is unlit — typically a single Light entry.
    [Export] private Godot.Collections.Array<InteractiveAction> _unlitActions = new();
    [Export] private PackedScene _lightOnEffectScene;
    [Export] private PackedScene _lightOffEffectScene;
    [Export] private PackedScene _lightLoopEffectScene;
    // Recipe scope for this station — Cooking.TryMatch only considers
    // recipes whose forgeType matches.
    [Export] private EForgeType _forgeType;
    // How long (seconds) a single cook job takes.
    [Export] private float _forgeTimeSeconds = 1.5f;
    public Vector3 hudPosition => _hudNode.GlobalPosition;

    private bool _active = true;
    private ForgeSimState _simState;
    private Fx _loopEffect;

    // CookingScreen subscribes when bound so the forge can hand off a
    // completed output to the player's inventory instead of dropping it.
    // Null = nobody listening; the forge spawns the loot itself.
    public Action<ForgeCompletion> deliveryCallback;
    // Fires every Tick the active cook job advances, plus once with `null`
    // when the job ends (complete OR cancelled). Lets the cooking screen
    // refresh its progress bar without polling state itself.
    public Action<ForgeJob> onForgeJobChanged;

    public ForgeSimState SimState => _simState;
    public ForgeJob ActiveForgeJob => _simState?.ActiveForgeJob;
    public ItemState[] ForgeSlots => _simState?.ForgeSlots;
    public bool IsLit => _active;
    public EForgeType ForgeType => _forgeType;

    private static readonly StringName AnimOn = "on";
    private static readonly StringName AnimOff = "off";

    public void OnSpawned(World world) { }

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
        if (action.verb == EActionVerb.Cook && _active)
        {
            GameClient gc = GameClient.Current;
            Player player = gc?.Player;
            if (gc?.cookingScreen != null && player != null)
            {
                gc.cookingScreen.Open(player, this);
            }
            return;
        }
        if (action.verb == EActionVerb.Light)
        {
            SetLit(true);
            return;
        }
        if (action.verb == EActionVerb.Douse)
        {
            SetLit(false);
            return;
        }
        SetLit(!_active);
    }

    // Toggle helper. Updates visuals, zones, fx, and cancels any in-flight
    // cook job when the flame goes out — items stay in ForgeSlots for
    // the player to reclaim on relight.
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
        if (!_active)
        {
            CancelForgeJob();
        }

        UpdateVisuals();
        _light.SetActive(_active);
        _damageZone?.SetActive(_active);
        _warmthZone?.SetActive(_active);

        PackedScene oneShot = _active ? _lightOnEffectScene : _lightOffEffectScene;
        if (oneShot != null)
        {
            Fx.Create(oneShot, GetParent(), Position);
        }
        UpdateLoopEffect();
    }

    public override void _PhysicsProcess(double delta)
    {
        ForgeJob job = _simState?.ActiveForgeJob;
        if (job == null)
        {
            return;
        }
        job.remainingSeconds -= (float)delta;
        if (job.remainingSeconds <= 0f)
        {
            CompleteForgeJob();
        }
        else
        {
            onForgeJobChanged?.Invoke(job);
        }
    }

    // Begin a cook job. Caller has already verified the slots match the
    // recipe via Cooking.TryMatch. Items remain in ForgeSlots until
    // completion — Cancel restores access without consuming anything.
    // Discovery is NOT credited here: it lands in CompleteForgeJob so a
    // cancelled cook never marks the recipe as learned.
    public void StartForgeJob(RecipeData recipe, ItemData output)
    {
        if (_simState == null || recipe == null || output == null)
        {
            return;
        }
        if (_simState.ActiveForgeJob != null)
        {
            return;
        }
        _simState.ActiveForgeJob = new ForgeJob
        {
            recipe = recipe,
            outputItem = output,
            remainingSeconds = _forgeTimeSeconds,
            totalSeconds = _forgeTimeSeconds,
        };
        onForgeJobChanged?.Invoke(_simState.ActiveForgeJob);
    }

    // Cancel any in-flight job. Items in ForgeSlots stay put — cancel is
    // an opt-out, not a destructive operation. Safe to call when no job is
    // active.
    public void CancelForgeJob()
    {
        if (_simState == null || _simState.ActiveForgeJob == null)
        {
            return;
        }
        _simState.ActiveForgeJob = null;
        onForgeJobChanged?.Invoke(null);
    }

    // Job ran to completion: record discovery, drain slots, and deliver
    // the output. Discovery runs directly here (not via the bound screen)
    // so an offscreen completion still credits the recipe. If a
    // CookingScreen is bound (deliveryCallback set), it takes the output
    // and decides between inventory and drop. Otherwise the forge spawns
    // the loot at its position for the player to walk back to.
    private void CompleteForgeJob()
    {
        ForgeJob job = _simState?.ActiveForgeJob;
        if (job == null)
        {
            return;
        }
        WorldSimState worldSim = World.Current?.WorldState?.SimState;
        bool wasNewDiscovery = job.recipe != null && (worldSim == null || !worldSim.DiscoveredRecipes.Contains(job.recipe));
        Cooking.RecordDiscovery(worldSim, new Cooking.MatchResult(job.recipe));

        if (_simState.ForgeSlots != null)
        {
            for (int i = 0; i < _simState.ForgeSlots.Length; i++)
            {
                _simState.ForgeSlots[i] = null;
            }
        }
        var completion = new ForgeCompletion
        {
            output = job.outputItem,
            wasNewDiscovery = wasNewDiscovery,
        };
        _simState.ActiveForgeJob = null;
        onForgeJobChanged?.Invoke(null);

        if (deliveryCallback != null)
        {
            deliveryCallback.Invoke(completion);
        }
        else if (completion.output != null)
        {
            // Offscreen completion — spawn the produced item as loot at the
            // forge. Light upward impulse so it doesn't intersect the mesh.
            World world = World.Current;
            world?.SpawnLoot(GlobalPosition + Vector3.Up, Vector3.Up * 2f, completion.output);
        }
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

    public static Forge Create(World world, ForgeSimState data)
    {
        var instance = data.Scene.Instantiate<Forge>();
        instance.Position = data.WorldPosition;
        instance._simState = data;
        var baseWorldPos = new Vector3I(
            Mathf.FloorToInt(data.WorldPosition.X),
            Mathf.FloorToInt(data.WorldPosition.Y),
            Mathf.FloorToInt(data.WorldPosition.Z)
        );
        instance._light.Initialize(world.WorldState, world, baseWorldPos);
        world.AddChild(instance);

        if (data.AutoLightAtNight)
        {
            double tod = world.WorldState.TimeOfDay01;
            bool isNight = tod < 0.25 || tod >= 0.75;
            data.Active = isNight;
        }
        instance._active = data.Active;
        instance.UpdateVisuals();
        // Snap to the spawned state — a streaming-in forge shouldn't fade up.
        instance._light.SetActive(instance._active, fade: false);
        instance._damageZone?.SetActive(instance._active);
        instance._warmthZone?.SetActive(instance._active);
        instance.UpdateLoopEffect();

        return instance;
    }
}

// Bundled completion info — the produced item plus a "first time" flag.
// The forge's deliveryCallback hands one of these to the bound CookingScreen
// so the in-screen announcement can read "New Recipe Discovered" vs
// "Cooking Complete" without re-querying state.
public struct ForgeCompletion
{
    public ItemData output;
    public bool wasNewDiscovery;
}
