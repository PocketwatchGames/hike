using System.Collections.Generic;
using Godot;

public enum ETrapdoorState
{
    Closed,   // idle, leaf up, walkable — armed if used as a trap
    Warning,  // telegraph window before the leaf drops
    Open,     // leaf swung away, collider off
    Disarmed, // wedged shut; ignores triggers
}

// The hinged floor leaf every trapdoor variant is built from. It owns two
// things: the leaf's swing (a hinge tween) and its walkability (toggling the
// leaf collider). Nothing else about the world moves — the pit the player
// falls into is authored below the panel, so opening is purely "swing the leaf
// and stop standing on it," gravity does the rest (see Door.cs for the same
// remove-the-floor-and-fall mechanic).
//
// Two roles, picked by scene composition:
//   * Manual — a Trapdoor host (or a Lever) drives SetOpen/Toggle directly.
//     No TriggerSource is wired and the state machine never engages.
//   * Triggered — a Trap host wires a TriggerSource at this panel; a body
//     stepping on the pad calls Trigger(), running warning -> drop (-> auto
//     re-close). This is the SpikeDeployer role, a leaf instead of spikes.
// One class serves both; the unused path lies dormant.
[GlobalClass]
public partial class TrapdoorPanel : Node3D, ITriggerable, IDisarmable
{
    [Export] public TrapdoorData data;
    // Pivot at the leaf's hinge edge, leaf parented under it. The leaf swings
    // about this node's local X — the scene seats it so a negative openAngleDeg
    // drops the free edge into the pit.
    [Export] private Node3D _hinge;
    // Walkable leaf collider (a StaticBody3D on Environment). Disabled == open.
    [Export] private StaticBody3D _leafCollider;
    // Optional — the perception-gated variant wires one so the trap can be
    // spotted and force-discovers itself the instant it springs.
    [Export] private Discoverable _discoverable;

    public bool IsOpen => _state == ETrapdoorState.Open;
    public ETrapdoorState State => _state;

    private ETrapdoorState _state = ETrapdoorState.Closed;
    private Sim _world;
    private Node _activeSource;
    // Sim-clock deadlines (GameTimeMs), so the cycle slows uniformly under
    // slow-mo and stays frame-rate independent — the clock gameplay timers use.
    private ulong _warningEndMs;
    private ulong _autoCloseMs;

    public override void _Ready()
    {
        _world = Sim.Current;
        if (_discoverable != null && data != null)
        {
            _discoverable.prominence = data.armedProminence;
        }
        // Start closed and solid. A trap panel does no per-frame work until a
        // body wakes it via Trigger(); only tick while a cycle is in flight.
        ApplyLeaf(open: false, animate: false);
        SetPhysicsProcess(false);
    }

    // ---- Manual role (Trapdoor host / Lever) -------------------------------

    public void SetOpen(bool open, bool animate = true)
    {
        _state = open ? ETrapdoorState.Open : ETrapdoorState.Closed;
        ApplyLeaf(open, animate);
        PackedScene fx = open ? data?.openEffect : data?.closeEffect;
        if (fx != null)
        {
            Fx.Create(fx, this, Vector3.Zero);
        }
    }

    public void Toggle()
    {
        SetOpen(!IsOpen);
    }

    // ---- Triggered role (Trap host) ----------------------------------------

    public void Trigger(Node source)
    {
        // Only an armed, closed panel springs. Ignore re-triggers mid-cycle
        // (matches SpikeDeployer) and a disarmed panel outright.
        if (_state != ETrapdoorState.Closed)
        {
            return;
        }
        _activeSource = source;
        float warning = data?.warningSeconds ?? 0f;
        if (warning > 0f)
        {
            EnterWarning(warning);
        }
        else
        {
            EnterOpen();
        }
    }

    public void Disarm()
    {
        _state = ETrapdoorState.Disarmed;
        _activeSource = null;
        ApplyLeaf(open: false, animate: false);
        SetPhysicsProcess(false);
    }

    public override void _PhysicsProcess(double delta)
    {
        ulong now = _world?.GameTimeMs ?? 0;
        switch (_state)
        {
            case ETrapdoorState.Warning:
                if (now >= _warningEndMs)
                {
                    EnterOpen();
                }
                break;
            case ETrapdoorState.Open:
                // autoCloseMs == 0 means "stay open forever" (crumbling floor);
                // it's only set when autoCloseSeconds > 0.
                if (_autoCloseMs != 0 && now >= _autoCloseMs)
                {
                    EnterClosed();
                }
                break;
        }
    }

    private void EnterWarning(float warningSeconds)
    {
        _state = ETrapdoorState.Warning;
        _warningEndMs = (_world?.GameTimeMs ?? 0) + (ulong)(warningSeconds * 1000f);
        if (data?.warningEffect != null)
        {
            Fx.Create(data.warningEffect, this, Vector3.Zero);
        }
        SetPhysicsProcess(true);
    }

    private void EnterOpen()
    {
        _state = ETrapdoorState.Open;
        ApplyLeaf(open: true, animate: true);
        if (data?.openEffect != null)
        {
            Fx.Create(data.openEffect, this, Vector3.Zero);
        }
        // A sprung trap is obvious — bump prominence and force the discovery,
        // so the now-open hole reads as noticed.
        if (_discoverable != null && data != null)
        {
            _discoverable.prominence = data.firedProminence;
        }
        _discoverable?.ForceDiscover();
        // Hit whoever's on the leaf, if the author wired drop damage. Body list
        // only exists for a TriggerSource source (a lever/chest source drops the
        // leaf cosmetically).
        if (data?.dropDamage != null && _activeSource is TriggerSource ts)
        {
            IReadOnlyList<Node3D> bodies = ts.BodiesInArea;
            for (int i = 0; i < bodies.Count; i++)
            {
                HitBody(bodies[i]);
            }
        }
        float autoClose = data?.autoCloseSeconds ?? 0f;
        if (autoClose > 0f)
        {
            _autoCloseMs = (_world?.GameTimeMs ?? 0) + (ulong)(autoClose * 1000f);
            SetPhysicsProcess(true);
        }
        else
        {
            // Permanently open — nothing left to tick.
            _autoCloseMs = 0;
            SetPhysicsProcess(false);
        }
    }

    private void EnterClosed()
    {
        _state = ETrapdoorState.Closed;
        _activeSource = null;
        ApplyLeaf(open: false, animate: true);
        if (data?.closeEffect != null)
        {
            Fx.Create(data.closeEffect, this, Vector3.Zero);
        }
        SetPhysicsProcess(false);
    }

    // Swing the hinge to the open/closed pose and match the leaf collider.
    private void ApplyLeaf(bool open, bool animate)
    {
        if (_leafCollider != null)
        {
            _leafCollider.GetNode<CollisionShape3D>("CollisionShape3D").Disabled = open;
        }
        if (_hinge == null)
        {
            return;
        }
        float target = Mathf.DegToRad(open ? (data?.openAngleDeg ?? -92f) : 0f);
        if (animate)
        {
            float secs = open ? (data?.openSeconds ?? 0.18f) : (data?.closeSeconds ?? 0.35f);
            CreateTween().TweenProperty(_hinge, "rotation:x", target, secs)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }
        else
        {
            Vector3 r = _hinge.Rotation;
            r.X = target;
            _hinge.Rotation = r;
        }
    }

    private void HitBody(Node3D body)
    {
        if (body == null || data?.dropDamage == null)
        {
            return;
        }
        HurtBox hurtBox = body.GetNodeOrNull<HurtBox>("HurtBox");
        hurtBox?.Hit(new HitInfo(data.dropDamage, this, Vector3.Down));
    }
}
