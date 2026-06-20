using System.Collections.Generic;
using Godot;

// See WanderFollowBehaviorData for the design summary. This runtime owns the
// per-dog cadence (wander → sniff → re-pick, or settle → rest) and the
// player-stillness tracking; the navigator handles the actual pathing. The dog
// has no dedicated sniff / lie-down clips, so both pauses just hold the idle
// loop (speed 0). Transitions (attack / wary / stay) are evaluated first so a
// threat or command takes effect immediately.
public partial class BehaviorWanderFollow : BehaviorBase
{
    private enum Phase
    {
        Moving,   // trotting toward _destination
        Sniffing, // paused at a wander point
        Resting,  // lying down (idle) beside a stopped player
    }

    private readonly WanderFollowBehaviorData _data;

    private Phase _phase;
    private bool _hasDestination;
    private Vector3 _destination;
    // True when the current destination is a rest spot beside a stopped player
    // rather than a wander point. Drives what happens on arrival and lets a
    // mid-leg change in the player's movement re-pick the right kind of goal.
    private bool _destIsRest;
    // True while beelining after a player who pulled beyond catchUpRadius. Lets
    // RunMoving re-issue the chase goal only as the player moves, and forces a
    // fresh wander pick on the tick we catch back up.
    private bool _catchingUp;
    private ulong _sniffUntilMs;

    // Player-stillness tracking. The player is "moving" until they've stayed
    // within playerStillRadius of _playerAnchor for stopGraceSeconds.
    private Vector3 _playerAnchor;
    private ulong _playerAnchorMs;

    // Throttle for companion_debug logging (~once per second). Diagnostic only.
    private ulong _nextDebugMs;
    private Vector3 _lastDebugPos;
    private bool _hasLastDebugPos;

    public BehaviorWanderFollow(WanderFollowBehaviorData data)
    {
        _data = data;
    }

    // Reset cross-tick state on every (re-)entry so returning from Stay / Wary
    // / Attack doesn't resume mid-leg against a stale destination or a sniff
    // timer left over from the last time this behavior ran.
    public override void OnEnter(Mob me, ulong time)
    {
        _phase = Phase.Moving;
        _hasDestination = false;
        _destIsRest = false;
        _catchingUp = false;
        _sniffUntilMs = 0;
        Player master = me.World?.player;
        _playerAnchor = master?.GlobalPosition ?? me.GlobalPosition;
        _playerAnchorMs = time;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        Player master = me.World?.player;
        if (master == null)
        {
            output.speed = 0f;
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        Vector3 playerPos = master.GlobalPosition;
        bool playerStopped = UpdatePlayerStillness(playerPos, time);

        switch (_phase)
        {
            case Phase.Resting:
                RunResting(me, playerPos, ref output);
                break;
            case Phase.Sniffing:
                RunSniffing(me, time, playerPos, playerStopped, ref output);
                break;
            default:
                RunMoving(me, time, playerPos, playerStopped, ref output);
                break;
        }

        if (CVars.companionDebug.Value && time >= _nextDebugMs)
        {
            _nextDebugMs = time + 1000;
            Vector3 pos = me.GlobalPosition;
            Vector3 toPlayer = playerPos - pos;
            toPlayer.Y = 0f;
            Vector3 toDest = _destination - pos;
            toDest.Y = 0f;
            // moved = how far the dog actually translated since the last log.
            // Near-zero moved while speed=1 (full catch-up) means the body is
            // wedged — distinguish "jammed on geometry" (horizVel high, moved~0)
            // from "no drive / over-damped" (horizVel~0).
            Vector3 hv = me.LinearVelocity;
            hv.Y = 0f;
            float movedXz = new Vector2(pos.X - _lastDebugPos.X, pos.Z - _lastDebugPos.Z).Length();
            string moved = _hasLastDebugPos ? $"{movedXz:F2}m" : "n/a";
            _lastDebugPos = pos;
            _hasLastDebugPos = true;
            // Probe what's ahead toward the live steer target (next waypoint if
            // any, else the destination) so a stall line shows WHY: both flags
            // true = pathed into a wall (nav bug); obstacleAhead only = a step
            // too tall / step-up declined. Only meaningful when actually moving.
            IReadOnlyList<Vector3> wps = me.Navigator.Waypoints;
            int wi = me.Navigator.WaypointIndex;
            Vector3 steerTarget = (wps.Count > 0 && wi < wps.Count) ? wps[wi] : _destination;
            string ahead = "n/a";
            if (_phase == Phase.Moving)
            {
                me.ProbeForwardObstacle(steerTarget - pos, out bool obstacleAhead, out bool wallAbove, out float maxStep);
                ahead = $"obstacleAhead={obstacleAhead} wallAbove={wallAbove} maxStep={maxStep:F2}";
            }
            // catchUpDistance is the gap at which the dog should be at full
            // catchUpSpeed; player gap >> wanderRadius means it's falling behind.
            GD.Print($"[companion] dog phase={_phase} catchUp={_catchingUp} playerGap={toPlayer.Length():F1}m " +
                     $"playerStopped={playerStopped} hasDest={_hasDestination} destIsRest={_destIsRest} " +
                     $"destGap={toDest.Length():F1}m speed={output.speed:F2} moved/s={moved} horizVel={hv.Length():F1} " +
                     $"navArrived={me.Navigator.HasArrived} navBlocked={me.Navigator.IsBlocked} " +
                     $"waypoints={wps.Count}@{wi} {ahead}");
        }

        return new BehaviorOutput(EBehaviorResult.Running);
    }

    // Returns true once the player has held within playerStillRadius of the
    // anchor for stopGraceSeconds; resets the anchor (and the clock) whenever
    // they leave that bubble.
    private bool UpdatePlayerStillness(Vector3 playerPos, ulong time)
    {
        Vector3 d = playerPos - _playerAnchor;
        d.Y = 0f;
        if (d.LengthSquared() > _data.playerStillRadius * _data.playerStillRadius)
        {
            _playerAnchor = playerPos;
            _playerAnchorMs = time;
            return false;
        }
        return time - _playerAnchorMs >= (ulong)(_data.stopGraceSeconds * 1000f);
    }

    private void RunMoving(Mob me, ulong time, Vector3 playerPos, bool playerStopped, ref AIOutput output)
    {
        // Catch-up: a moving player who pulled beyond catchUpRadius is chased
        // directly — no wander point, no sniff — until the dog is back inside
        // the radius. Re-issue the chase goal only as the player moves
        // (catchUpRetargetDistance) so we don't reset the navigator's repath
        // throttle every frame. SpeedForLeg gives full catchUpSpeed here since
        // the goal is far.
        Vector3 toPlayer = playerPos - me.GlobalPosition;
        toPlayer.Y = 0f;
        if (!playerStopped && toPlayer.LengthSquared() > _data.catchUpRadius * _data.catchUpRadius)
        {
            if (!_catchingUp || _destination.DistanceSquaredTo(playerPos) > _data.catchUpRetargetDistance * _data.catchUpRetargetDistance)
            {
                _destination = playerPos;
                me.Navigator.Goto(_destination, _data.arrivalDistance);
            }
            _catchingUp = true;
            _destIsRest = false;
            _hasDestination = true;
            output.speed = SpeedForLeg(me);
            return;
        }
        // Back within catchUpRadius (or the player stopped) — drop the chase so
        // the next pick is a fresh wander/rest goal and normal sniffing resumes.
        if (_catchingUp)
        {
            _catchingUp = false;
            _hasDestination = false;
        }

        // Pick (or re-pick) a destination when we don't have one, or when the
        // player's stop/go state no longer matches the kind of goal we're
        // heading to — player stopped while we were wandering → go rest beside
        // them; player set off again while we were heading in → wander instead.
        if (!_hasDestination || playerStopped != _destIsRest)
        {
            PickDestination(playerPos, playerStopped);
        }

        me.Navigator.Goto(_destination, _data.arrivalDistance);
        output.speed = SpeedForLeg(me);

        if (me.Navigator.HasArrived || me.Navigator.IsBlocked)
        {
            _hasDestination = false;
            me.Navigator.Stop();
            if (_destIsRest)
            {
                _phase = Phase.Resting;
            }
            else
            {
                double sniff = GD.RandRange((double)_data.sniffTimeRange.X, (double)_data.sniffTimeRange.Y);
                _sniffUntilMs = time + (ulong)(sniff * 1000.0);
                _phase = Phase.Sniffing;
            }
        }
    }

    // Move speed for the current leg, lerped from moveSpeed (at the
    // destination) up to catchUpSpeed (catchUpDistance or more away). Because
    // destinations are picked around the player, a destination far from the dog
    // means the player has pulled ahead — so the dog ambles on short legs and
    // speeds up to close a big gap, with no separate beeline state.
    private float SpeedForLeg(Mob me)
    {
        Vector3 toDest = _destination - me.GlobalPosition;
        toDest.Y = 0f;
        float t = Mathf.Clamp(toDest.Length() / Mathf.Max(0.001f, _data.catchUpDistance), 0f, 1f);
        return Mathf.Lerp(_data.moveSpeed, _data.catchUpSpeed, t);
    }

    private void RunSniffing(Mob me, ulong time, Vector3 playerPos, bool playerStopped, ref AIOutput output)
    {
        output.speed = 0f;
        // Cut the sniff short if the player has pulled far enough that we need
        // to catch up — don't finish sniffing while they run off. RunMoving
        // takes over the chase next tick.
        Vector3 toPlayer = playerPos - me.GlobalPosition;
        toPlayer.Y = 0f;
        bool needCatchUp = !playerStopped && toPlayer.LengthSquared() > _data.catchUpRadius * _data.catchUpRadius;
        if (time >= _sniffUntilMs || needCatchUp)
        {
            PickDestination(playerPos, playerStopped);
            _phase = Phase.Moving;
        }
    }

    private void RunResting(Mob me, Vector3 playerPos, ref AIOutput output)
    {
        output.speed = 0f;
        // Face the player while lying down.
        Vector3 toPlayer = playerPos - me.GlobalPosition;
        toPlayer.Y = 0f;
        if (toPlayer.LengthSquared() > 0.0001f)
        {
            output.yaw = Mathf.Atan2(toPlayer.X, toPlayer.Z);
        }
        // Only rouse once the player has wandered beyond getUpRadius — a few
        // idle steps from the player don't disturb a resting dog.
        if (toPlayer.LengthSquared() > _data.getUpRadius * _data.getUpRadius)
        {
            _hasDestination = false;
            _phase = Phase.Moving;
        }
    }

    private void PickDestination(Vector3 playerPos, bool playerStopped)
    {
        float minR = playerStopped ? _data.restMinDistance : _data.minWanderDistance;
        float maxR = playerStopped ? _data.restApproachRadius : _data.wanderRadius;
        maxR = Mathf.Max(maxR, minR);
        float angle = GD.Randf() * Mathf.Tau;
        float radius = (float)GD.RandRange((double)minR, (double)maxR);
        _destination = playerPos + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
        _destIsRest = playerStopped;
        _hasDestination = true;
    }
}
