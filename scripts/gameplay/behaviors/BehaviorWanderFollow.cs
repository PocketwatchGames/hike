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
    private ulong _sniffUntilMs;

    // Player-stillness tracking. The player is "moving" until they've stayed
    // within playerStillRadius of _playerAnchor for stopGraceSeconds.
    private Vector3 _playerAnchor;
    private ulong _playerAnchorMs;

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
        if (time >= _sniffUntilMs)
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
