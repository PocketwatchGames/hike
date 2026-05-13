using System;
using Godot;

// Campfire / cooking station. Standalone from Torch — they share a similar
// lit/doused shape in the editor (animator, light, warmth/damage zones,
// on/off fx) but the cooking workflow is forge-specific so the two
// classes don't share a base.
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
    // How long (seconds) a single cook job takes. Per-recipe override comes
    // later once the player can pick a target cook time.
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
    public void StartForgeJob(RecipeData recipe, ItemData output, bool isHighQuality)
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
            isHighQuality = isHighQuality,
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
    // the output. Discovery work happens BEFORE the drain so the slot
    // contents are still readable for RecordDiscovery's min-count tracking,
    // and runs directly here (not via the bound screen) so an offscreen
    // completion still credits the recipe. If a CookingScreen is bound
    // (deliveryCallback set), it takes the output and decides between
    // inventory and drop. Otherwise the forge spawns the loot at its
    // position for the player to walk back to.
    private void CompleteForgeJob()
    {
        ForgeJob job = _simState?.ActiveForgeJob;
        if (job == null)
        {
            return;
        }
        WorldSimState worldSim = World.Current?.WorldState?.SimState;
        bool wasNewDiscovery = job.recipe != null && (worldSim == null || !worldSim.DiscoveredRecipes.ContainsKey(job.recipe));
        bool wasNewHighQualityDiscovery = false;
        if (job.isHighQuality && job.recipe != null && worldSim != null)
        {
            wasNewHighQualityDiscovery = wasNewDiscovery || !worldSim.DiscoveredRecipes[job.recipe].discoveredHighQuality;
        }
        var match = new Cooking.MatchResult(job.recipe, job.isHighQuality);
        Cooking.RecordDiscovery(worldSim, match, _simState.ForgeSlots);

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
            isHighQuality = job.isHighQuality,
            wasNewDiscovery = wasNewDiscovery,
            wasNewHighQualityDiscovery = wasNewHighQualityDiscovery,
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

    private void UpdateVisuals()
    {
        if (_animator == null)
        {
            GD.PushError($"Forge '{Name}' has no _animator wired");
            return;
        }
        _animator.Play(_active ? AnimOn : AnimOff);
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
        instance._light.SetActive(instance._active);
        instance._damageZone?.SetActive(instance._active);
        instance._warmthZone?.SetActive(instance._active);
        instance.UpdateLoopEffect();

        return instance;
    }
}

// Bundled completion info — recipe context plus the produced item. The
// forge's deliveryCallback hands one of these to the bound CookingScreen
// so the announcement system can decide between "New Recipe Discovered"
// vs "Cooking Complete" without re-querying state.
public struct ForgeCompletion
{
    public ItemData output;
    public bool isHighQuality;
    public bool wasNewDiscovery;
    public bool wasNewHighQualityDiscovery;
}
