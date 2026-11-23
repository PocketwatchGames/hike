using System;
using Godot;

// What a world script can make a mob DO (WorldScriptApi): go somewhere, trail
// the controlled player, vanish, and reappear somewhere else. Scripted control
// OVERRIDES the brain — TickAI hands the tick here first — so it works on any
// mob without a brain node authored for it.
//
// Once a script has placed a mob it STAYS placed: arriving from a SendTo or a
// Reappear, or stopping a follow, leaves it HOLDING there (over a mob it was
// sent to, following that mob as it moves) until the next command. Release
// hands it back to its brain. A flier holds and moves at
// SimData.scriptHoverHeight rather than its own hoverHeight.
//
// A mob a script moves, follows or reappears becomes PERSISTENT (like the
// companion): its node is never evicted with the chunk it spawned in, which a
// script can send it arbitrarily far from. Runtime only, apart from following
// and hiding (MobSimState.ScriptFollowing / Hidden): a move, a hold and a
// `then` are lost when the run ends.
public partial class Mob
{
    private Vector3? _scriptGoal;
    // A moving goal: when set, the move steers at this node's current position
    // (refreshed every tick into _scriptGoal) instead of a fixed spot.
    private Node3D _scriptTarget;
    private float _scriptSpeed;
    private Action _scriptThen;

    // Where a script left the mob: a spot, or a node it stays over.
    private Vector3? _holdAt;
    private Node3D _holdTarget;

    // The reappear: a vanish whose far end lands somewhere else instead of
    // removing the mob.
    private bool _reappearing;
    private bool _reappearArriving;
    private float _reappearElapsed;
    private Vector3 _reappearFrom;
    private Vector3 _reappearAt;
    private Node3D _reappearTarget;
    private Action _reappearThen;
    // A Reappear asked for while a vanish is still rising, run as soon as the
    // mob is hidden; and a Vanish asked for mid-reappear, run on landing. Two
    // trigger boxes crossed in one step ask for both in either order.
    private Action _afterVanish;
    private bool _vanishAfterReappear;
    // A vanish scheduled on the sim clock (ScriptVanishAfter); 0 = none. Any
    // later scripted command cancels it.
    private ulong _vanishAtMs;
    private uint _reappearLayer;
    private uint _reappearMask;
    private uint _reappearHurtLayer;

    public bool IsScriptMoving => _scriptGoal.HasValue;

    public bool IsScriptFollowing => _simState?.ScriptFollowing ?? false;

    public bool IsHidden => _simState?.Hidden ?? false;

    private bool IsFlier => mobData != null && mobData.CanFly;

    // Go to `target` wherever it is — the goal follows it — then hold over it.
    public void ScriptMoveTo(Node3D target, bool run, Action then)
    {
        ScriptMoveTo(target.GlobalPosition, run, then);
        _scriptTarget = target;
    }

    // Walk (or run) to `goal`, run `then`, and hold there. Replaces any earlier
    // scripted move or hold, and stops a follow.
    public void ScriptMoveTo(Vector3 goal, bool run, Action then)
    {
        ClearScripted();
        if (_simState.ScriptFollowing)
        {
            _simState.ScriptFollowing = false;
        }
        _world?.PromoteMobToPersistent(this);
        SimData data = _world?.SimData;
        _scriptGoal = goal;
        _scriptSpeed = run ? (data?.scriptRunSpeed ?? 1f) : (data?.scriptWalkSpeed ?? 0.5f);
        _scriptThen = then;
    }

    // Start or stop trailing the controlled player. Following files the mob as a
    // persistent entity (like the companion) so it is never streamed out behind
    // the party; stopping leaves it persistent, holding wherever it stopped.
    public void ScriptFollow(bool follow)
    {
        if (_simState == null)
        {
            return;
        }
        ClearScripted();
        _simState.ScriptFollowing = follow;
        if (follow)
        {
            _world?.PromoteMobToPersistent(this);
        }
        else
        {
            _navigator?.Stop();
            _holdAt = GlobalPosition;
        }
    }

    // Hand the mob back to its brain: no move, no hold, no follow.
    public void ScriptRelease()
    {
        ClearScripted();
        if (_simState != null)
        {
            _simState.ScriptFollowing = false;
        }
        _navigator?.Stop();
    }

    private void ClearScripted()
    {
        _vanishAtMs = 0;
        _scriptGoal = null;
        _scriptTarget = null;
        _scriptThen = null;
        _holdAt = null;
        _holdTarget = null;
    }

    // Rise and fade as an escape does, then stay HIDDEN (not removed) until a
    // Reappear brings the mob back. Drops any scripted move or hold.
    public void Vanish()
    {
        // The line goes the moment she starts to leave, not once she is gone.
        Hush();
        _afterVanish = null;
        if (_reappearing)
        {
            _vanishAfterReappear = true;
            return;
        }
        if (_vanishing || IsHidden)
        {
            return;
        }
        ClearScripted();
        SimData data = _world?.SimData;
        CaptureBodyLayers();
        BeginVanish(data?.scriptReappearAscent ?? 4f, data?.scriptReappearSeconds ?? 1f, hide: true);
    }

    // Vanish `seconds` of sim time from now, unless a scripted command (another
    // Reappear, a move, a follow) comes first — the grace a script gives a
    // player who steps out of a box and straight back in.
    public void ScriptVanishAfter(float seconds)
    {
        if (IsHidden || _vanishing)
        {
            return;
        }
        _vanishAtMs = (_world?.GameTimeMs ?? 0) + (ulong)(Mathf.Max(0f, seconds) * 1000f);
    }

    // Called every physics tick, ahead of the reappear: one comparison.
    private void TickScriptedVanish()
    {
        if (_vanishAtMs != 0 && _world != null && _world.GameTimeMs >= _vanishAtMs)
        {
            _vanishAtMs = 0;
            Vanish();
        }
    }

    // Already holding over this spot (or this target), in plain view: a
    // Reappear here has nothing to do but its `then`.
    private bool IsHoldingAt(Vector3 at, Node3D target)
    {
        if (IsHidden || _vanishing || _reappearing || !_holdAt.HasValue)
        {
            return false;
        }
        if (target != null && _holdTarget == target)
        {
            return true;
        }
        Vector3 d = _holdAt.Value - at;
        d.Y = 0f;
        return d.LengthSquared() <= SameSpotDistance * SameSpotDistance;
    }

    // Flat metres within which a Reappear counts as "where it already is".
    private const float SameSpotDistance = 0.5f;

    // Out of sight, intangible and inert, and out of the spatial hash so no
    // other mob perceives it. Saved (MobSimState.Hidden).
    private void EnterHidden()
    {
        if (!_hidingLayersCaptured)
        {
            CaptureBodyLayers();
        }
        _simState.Hidden = true;
        _navigator?.Stop();
        Freeze = true;
        LinearVelocity = Vector3.Zero;
        CollisionLayer = 0;
        CollisionMask = 0;
        if (_hurtBox != null)
        {
            _hurtBox.CollisionLayer = 0;
        }
        (_vanishFade as IVanishFade)?.SetFade(0f);
        Visible = false;
        _world?.MobSpatialHash.Remove(this);
        Hush();
        Action next = _afterVanish;
        _afterVanish = null;
        next?.Invoke();
    }

    // The body's collision as it stands, for a reappear to restore. A mob that
    // spawns hidden has never been tangible in this session, so it captures
    // what its own setup left.
    private bool _hidingLayersCaptured;

    private void CaptureBodyLayers()
    {
        _reappearLayer = CollisionLayer;
        _reappearMask = CollisionMask;
        _reappearHurtLayer = _hurtBox?.CollisionLayer ?? 0;
        _hidingLayersCaptured = true;
    }

    // Fade back in over `target` — where it is at the moment the mob comes back
    // in — then hold over it and run `then`.
    public void Reappear(Node3D target, Action then = null)
    {
        if (IsHoldingAt(target.GlobalPosition, target))
        {
            _vanishAtMs = 0;
            then?.Invoke();
            return;
        }
        if (_vanishing)
        {
            _afterVanish = () => Reappear(target, then);
            return;
        }
        Reappear(target.GlobalPosition, then);
        if (_reappearing)
        {
            _reappearTarget = target;
        }
    }

    // Fade back in at `at`, hold there, then run `then`. A visible mob rises and
    // fades out first, as an escape does; a hidden one only fades in. Any
    // scripted move or hold is dropped, and a follow resumes on landing.
    public void Reappear(Vector3 at, Action then = null)
    {
        _vanishAtMs = 0;
        _world?.PromoteMobToPersistent(this);
        if (IsHoldingAt(at, null))
        {
            then?.Invoke();
            return;
        }
        if (_vanishing)
        {
            _afterVanish = () => Reappear(at, then);
            return;
        }
        _vanishAfterReappear = false;
        if (_reappearing)
        {
            // Already on the way somewhere: land here instead.
            _reappearAt = at;
            _reappearTarget = null;
            _reappearThen = then;
            return;
        }
        bool fromHidden = IsHidden;
        _scriptGoal = null;
        _scriptTarget = null;
        _scriptThen = null;
        _holdAt = null;
        _holdTarget = null;
        _reappearing = true;
        _reappearElapsed = 0f;
        _reappearFrom = GlobalPosition;
        _reappearAt = at;
        _reappearTarget = null;
        _reappearThen = then;
        if (fromHidden)
        {
            // Already out of sight: straight to the arriving half.
            _reappearArriving = true;
            _simState.Hidden = false;
            GlobalPosition = ArrivalPosition(1f);
            Visible = true;
            _world?.MobSpatialHash.Add(this);
            return;
        }
        _reappearArriving = false;
        Hush();
        CaptureBodyLayers();
        Freeze = true;
        CollisionLayer = 0;
        CollisionMask = 0;
        if (_hurtBox != null)
        {
            _hurtBox.CollisionLayer = 0;
        }
    }

    // Where the arriving half puts the mob, `up` (1 -> 0) of the way through
    // its drop: over the landing spot (re-read off the target, which may have
    // moved) at its scripted height, dropping in from above — never from above
    // a ceiling, so in a passage it simply fades in where it lands.
    private Vector3 ArrivalPosition(float up)
    {
        if (_reappearTarget != null && GodotObject.IsInstanceValid(_reappearTarget) && _reappearTarget.IsInsideTree())
        {
            _reappearAt = _reappearTarget.GlobalPosition;
        }
        float ascent = _world?.SimData?.scriptReappearAscent ?? 4f;
        float landing = ScriptedHeightOver(_reappearAt, out float cap);
        return new Vector3(_reappearAt.X, Mathf.Min(landing + ascent * up, cap), _reappearAt.Z);
    }

    // The height a scripted mob holds over `spot` (a floor point: a waypoint,
    // or a target's feet): SimData.scriptHoverHeight above it for a flier, but
    // under any ceiling there, so it stays on the slice the camera cuts to.
    // `cap` is the highest it may be in that column (the ceiling, less a body).
    private float ScriptedHeightOver(Vector3 spot, out float cap)
    {
        cap = float.MaxValue;
        if (!IsFlier)
        {
            return spot.Y;
        }
        float y = spot.Y + (_world?.SimData?.scriptHoverHeight ?? 2.5f);
        WorldState ws = _world?.WorldState;
        if (ws != null)
        {
            int ceiling = CeilingYAbove(ws, Mathf.FloorToInt(spot.X), Mathf.FloorToInt(spot.Y) + 1, Mathf.FloorToInt(spot.Z));
            if (ceiling != int.MaxValue)
            {
                cap = Mathf.Max(spot.Y, ceiling - ScriptedCeilingClearance);
                y = Mathf.Min(y, cap);
            }
        }
        return y;
    }

    // Metres kept between a scripted flier and a ceiling over it.
    private const float ScriptedCeilingClearance = 1f;

    // Advances a reappear. Returns true while it runs so the caller skips AI
    // and locomotion, like TickVanish.
    private bool TickReappear(float delta)
    {
        if (!_reappearing)
        {
            return false;
        }
        SimData data = _world?.SimData;
        float ascent = data?.scriptReappearAscent ?? 4f;
        float half = Mathf.Max(0.01f, data?.scriptReappearSeconds ?? 1f);
        _reappearElapsed += delta;
        float t = Mathf.Clamp(_reappearElapsed / half, 0f, 1f);
        if (!_reappearArriving)
        {
            float rise = Mathf.Sin(t * Mathf.Pi * 0.5f);
            GlobalPosition = _reappearFrom + Vector3.Up * (ascent * rise);
            (_vanishFade as IVanishFade)?.SetFade(1f - t);
            if (t >= 1f)
            {
                _reappearArriving = true;
                _reappearElapsed = 0f;
                GlobalPosition = ArrivalPosition(1f);
            }
            return true;
        }
        float fall = Mathf.Sin(t * Mathf.Pi * 0.5f);
        GlobalPosition = ArrivalPosition(1f - fall);
        (_vanishFade as IVanishFade)?.SetFade(t);
        if (t >= 1f)
        {
            _reappearing = false;
            Freeze = false;
            LinearVelocity = Vector3.Zero;
            CollisionLayer = _reappearLayer;
            CollisionMask = _reappearMask;
            if (_hurtBox != null)
            {
                _hurtBox.CollisionLayer = _reappearHurtLayer;
            }
            _simState.WorldPosition = GlobalPosition;
            if (!_simState.ScriptFollowing)
            {
                _holdAt = _reappearAt;
                _holdTarget = _reappearTarget;
            }
            _reappearTarget = null;
            Action then = _reappearThen;
            _reappearThen = null;
            if (_vanishAfterReappear)
            {
                // Sent away again before it landed: skip what landing was for.
                _vanishAfterReappear = false;
                Vanish();
                return true;
            }
            then?.Invoke();
        }
        return true;
    }

    // Speaks one branch of this mob's conversation — by its conversation key —
    // as a standing line: typed over the mob, its paragraphs advancing on their
    // own, the last one staying up. Rendered for the controlled player in the
    // branch's language, else the mob's. A branch that needs a conversation
    // (choices, or actions on its lines) is refused rather than spoken with
    // half of it dropped. False, with an error, when it cannot be spoken.
    public bool Say(string key)
    {
        ConversationData conversation = _simState?.Conversation;
        ConversationBranch branch = conversation?.FindBranch(key);
        if (branch == null)
        {
            GD.PushError($"Mob '{_simState?.Name}': its conversation has no branch '{key}' to say.");
            return false;
        }
        if (!IsSpeakable(branch, out string why))
        {
            GD.PushError($"Mob '{_simState?.Name}': branch '{key}' cannot be said as a standing line — {why}. "
                + "Open it with a conversation instead.");
            return false;
        }
        LanguageData language = branch.language ?? _simState.Language ?? mobData?.language;
        Player reader = GameClient.Current?.Player;
        var lines = new System.Collections.Generic.List<string>();
        int count = branch.lines?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            StringName locKey = branch.lines[i]?.locKey;
            if (locKey != null && !locKey.IsEmpty)
            {
                lines.Add(LanguageText.RenderBbcode(Loc.Get(locKey), language, reader));
            }
        }
        if (lines.Count == 0)
        {
            GD.PushError($"Mob '{_simState?.Name}': branch '{key}' has no text to say.");
            return false;
        }
        StringName nameKey = conversation.SpeakerLocKey(_world?.WorldState?.SimState);
        string name = nameKey == null || nameKey.IsEmpty ? null : Loc.Get(nameKey);
        GameClient.Current?.onStandingLine?.Invoke(this, key, lines, name);
        return true;
    }

    // A standing line has nobody to answer it and no moment the player moves
    // past it, so choices and line actions have nowhere to happen.
    private static bool IsSpeakable(ConversationBranch branch, out string why)
    {
        if (branch.exitGroup != null && !branch.exitGroup.IsEmpty)
        {
            why = $"it goes on to the choices '{branch.exitGroup}'";
            return false;
        }
        int count = branch.lines?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            ConversationLine line = branch.lines[i];
            if ((line?.startActions?.Count ?? 0) > 0 || (line?.completeActions?.Count ?? 0) > 0)
            {
                why = "a paragraph has actions";
                return false;
            }
        }
        why = null;
        return true;
    }

    // Lowers the standing line — only if it is still branch `key`, when one is
    // given, so a zone's exit cannot silence the line a later zone raised.
    public void Hush(string key = null)
    {
        GameClient.Current?.onHushStandingLine?.Invoke(this, key);
    }

    // Steers a scripted move, follow or hold in place of the brain. Returns true
    // when it did, so TickAI skips the behaviors this tick.
    private bool TickScripted(float deltaTime, ref AIOutput output)
    {
        Vector3 goal;
        float arrive;
        float speed;
        SimData data = _world?.SimData;
        if (_scriptGoal.HasValue)
        {
            if (_scriptTarget != null)
            {
                if (GodotObject.IsInstanceValid(_scriptTarget) && _scriptTarget.IsInsideTree())
                {
                    _scriptGoal = _scriptTarget.GlobalPosition;
                }
                else
                {
                    // Gone (killed, streamed out): finish at its last spot.
                    _scriptTarget = null;
                }
            }
            goal = _scriptGoal.Value;
            arrive = data?.scriptArriveDistance ?? 1f;
            speed = _scriptSpeed;
            Vector3 to = goal - GlobalPosition;
            to.Y = 0f;
            if (to.LengthSquared() <= arrive * arrive)
            {
                _holdAt = goal;
                _holdTarget = _scriptTarget;
                _scriptGoal = null;
                _scriptTarget = null;
                _navigator?.Stop();
                Action then = _scriptThen;
                _scriptThen = null;
                then?.Invoke();
                return true;
            }
        }
        else if (_simState.ScriptFollowing && _world?.player != null)
        {
            goal = _world.player.GlobalPosition;
            float follow = data?.scriptFollowDistance ?? 3f;
            arrive = data?.scriptFollowStopDistance ?? 2f;
            Vector3 to = goal - GlobalPosition;
            to.Y = 0f;
            // Hold inside the follow distance, facing the player; the gap
            // between it and the stop distance keeps her from jittering.
            if (to.LengthSquared() <= follow * follow
                && (_navigator == null || _navigator.CurrentState == MobNavigator.State.Idle || _navigator.HasArrived))
            {
                _navigator?.Stop();
                output.speed = 0f;
                if (to.LengthSquared() > 0.01f)
                {
                    output.yaw = Mathf.Atan2(to.X, to.Z);
                }
                if (IsFlier)
                {
                    output.airborne = true;
                    output.flyTargetY = ScriptedHeightOver(goal, out _);
                }
                return true;
            }
            speed = to.LengthSquared() > follow * follow * 4f
                ? data?.scriptRunSpeed ?? 1f
                : data?.scriptWalkSpeed ?? 0.5f;
        }
        else if (_holdAt.HasValue)
        {
            if (_holdTarget != null)
            {
                if (GodotObject.IsInstanceValid(_holdTarget) && _holdTarget.IsInsideTree())
                {
                    _holdAt = _holdTarget.GlobalPosition;
                }
                else
                {
                    _holdTarget = null;
                }
            }
            goal = _holdAt.Value;
            arrive = data?.scriptHoldDistance ?? 0.5f;
            speed = data?.scriptWalkSpeed ?? 0.5f;
            Vector3 to = goal - GlobalPosition;
            to.Y = 0f;
            if (!IsFlier && to.LengthSquared() <= arrive * arrive)
            {
                _navigator?.Stop();
                output.speed = 0f;
                return true;
            }
        }
        else
        {
            return false;
        }

        output.speed = speed;
        output.pathSuccessDistance = arrive;
        if (IsFlier)
        {
            // A flier has no ground grid to path on: straight at it, with the
            // flight layer's own climb-over-obstacles handling the terrain.
            output.airborne = true;
            output.pathTarget = goal;
            output.flyTargetY = ScriptedHeightOver(goal, out _);
            return true;
        }
        if (_navigator != null)
        {
            if (_navigator.CurrentState != MobNavigator.State.Goto || _navigator.Goal.DistanceSquaredTo(goal) > 1f)
            {
                _navigator.Goto(goal, arrive);
            }
            _navigator.WriteSteering(deltaTime, ref output);
        }
        return true;
    }
}
