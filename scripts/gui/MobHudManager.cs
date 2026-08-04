using System.Collections.Generic;
using Godot;

// Leases a small pool of MobHUD instances to the mobs that currently need one.
//
// Every loaded mob used to own a MobHUD outright: a 17-node Control subtree with
// its own _Process. In the default world that is 139 of them — ~2360 resident
// nodes and 139 engine→C# dispatches per frame — to draw the handful of bars
// actually on screen. The overwhelming majority of loaded mobs are undetected,
// unhurt, and far out in the streaming radius, so their HUD resolves to "draw
// nothing" every single frame.
//
// This node owns the population instead. One _Process walks the live mobs in a
// tight managed loop, applies a cheap gate (WantsHud), and hands a pooled HUD to
// the ones that pass. Leased HUDs are ticked directly from here, so per-frame
// dispatch tracks the number of VISIBLE huds rather than the number of loaded
// mobs, and the pool only ever grows to the concurrent high-water mark.
[GlobalClass]
public partial class MobHudManager : Node
{
    private Camera3D _camera;

    // Live mobs and the HUD each currently holds (null = not leased). Kept as
    // two lock-step lists rather than a dictionary: this is walked in full every
    // frame, and the walk is the whole point of the class.
    private readonly List<Mob> _mobs = new();
    private readonly List<MobHUD> _leased = new();

    // Idle HUDs, keyed by the scene they were instantiated from. Mobs all share
    // one mob_hud.tscn today, but hudScene is per-Mob, so a species with its own
    // HUD scene must not be handed another species' instance.
    private readonly Dictionary<PackedScene, List<MobHUD>> _pool = new();

    public void Init(Camera3D camera)
    {
        _camera = camera;
    }

    public void Register(Mob mob)
    {
        if (mob == null || mob.hudScene == null)
        {
            return;
        }
        _mobs.Add(mob);
        _leased.Add(null);
    }

    public void Unregister(Mob mob)
    {
        int index = _mobs.IndexOf(mob);
        if (index < 0)
        {
            return;
        }
        Release(index);
        _mobs.RemoveAtSwap(index);
        _leased.RemoveAtSwap(index);
    }

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("MobHudManager.Process");
        // Debug overlays show a readout over every on-screen mob, so they force
        // every mob to hold a HUD. Read the cvars once, not per mob.
        bool debugOverlays = CVars.debugPlayerPerception.Value
            || CVars.debugMobPerception.Value
            || CVars.debugMobPosition.Value;

        for (int i = _mobs.Count - 1; i >= 0; i--)
        {
            Mob mob = _mobs[i];
            if (mob == null || !IsInstanceValid(mob))
            {
                // Belt-and-braces: a mob freed without an Unregister would
                // otherwise strand its lease.
                Release(i);
                _mobs.RemoveAtSwap(i);
                _leased.RemoveAtSwap(i);
                continue;
            }

            bool wants = WantsHud(mob, debugOverlays);
            MobHUD hud = _leased[i];
            if (hud == null)
            {
                if (!wants)
                {
                    continue;
                }
                hud = Acquire(mob);
                _leased[i] = hud;
            }

            // Tick even when the gate has gone false — that's what lets the bar
            // finish its fade-out before the HUD goes back in the pool.
            bool stillShowing = hud.Tick(delta);
            if (!wants && !stillShowing)
            {
                Release(i);
            }
        }
    }

    // Cheap superset of "this mob's HUD would draw something". It must never be
    // false while the HUD would show anything (that would silently drop bars),
    // but it may be true when nothing shows — Tick resolves the truth and the
    // lease is dropped on the next frame.
    //
    // Ordering is deliberate: the flags are plain managed reads, while maxHealth
    // and maxArmor fold stat-modifier lists, so the injured test runs only for
    // the few mobs that are player-side, visible, or engaged.
    private static bool WantsHud(Mob mob, bool debugOverlays)
    {
        if (debugOverlays)
        {
            return true;
        }
        // Anything at least Detected can surface a bar, the status strip, or the
        // level pips.
        if (mob.playerPerceptionState != EPlayerPerceptionState.Hidden)
        {
            return true;
        }
        if (!mob.alive || mob.burrowed)
        {
            return false;
        }
        // Fully hidden and alive: only the health bar can still show, and only
        // for a mob the player owns, can see, or has engaged.
        bool healthEligible = Teams.AreAllied(mob.ActorTeam, ETeam.Player)
            || mob.playerCanSee
            || mob.triggered;
        if (!healthEligible)
        {
            return false;
        }
        return mob.health < mob.maxHealth || mob.armor < mob.maxArmor;
    }

    private MobHUD Acquire(Mob mob)
    {
        if (!_pool.TryGetValue(mob.hudScene, out List<MobHUD> free))
        {
            free = new List<MobHUD>();
            _pool[mob.hudScene] = free;
        }

        MobHUD hud;
        if (free.Count > 0)
        {
            hud = free[free.Count - 1];
            free.RemoveAt(free.Count - 1);
        }
        else
        {
            hud = MobHUD.Create(mob.hudScene, _camera, GetParent());
        }
        hud.Bind(mob);
        return hud;
    }

    private void Release(int index)
    {
        MobHUD hud = _leased[index];
        if (hud == null)
        {
            return;
        }
        _leased[index] = null;
        hud.Unbind();
        // Keyed off the HUD's own source scene, not the mob's — the mob may
        // already be freed by the time its lease is released.
        if (!_pool.TryGetValue(hud.SourceScene, out List<MobHUD> free))
        {
            free = new List<MobHUD>();
            _pool[hud.SourceScene] = free;
        }
        free.Add(hud);
    }
}
