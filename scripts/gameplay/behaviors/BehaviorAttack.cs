using Godot;

public partial class BehaviorAttack : BehaviorBase
{
    private readonly AttackBehaviorData _data;
    private Vector3? _repositionPoint;
    private ulong _weaponCooldownUntilMs;

    public BehaviorAttack(AttackBehaviorData data)
    {
        _data = data;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        Player target = targetPerception.pawnTarget;
        if (target == null)
        {
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        // Yell once on first sighting this engagement. MobAI clears the flag
        // when perception drops so the next engagement yells again.
        if (!me.yelled && targetPerception.canSee)
        {
            output.yell = true;
            me.yelled = true;
        }

        Vector3 targetPos = targetPerception.canSee ? target.GlobalPosition : targetPerception.lastKnownPosition;
        output.targetPos = targetPos;

        Vector3 diff = targetPos - me.weaponPosition;
        Vector2 dir2d = new Vector2(diff.X, diff.Z);
        float dist2d = dir2d.Length();
        if (dist2d > 0.0001f)
        {
            // yaw is atan2(x, z) to match Mob._PhysicsProcess's yaw convention.
            output.yaw = Mathf.Atan2(dir2d.X, dir2d.Y);
        }

        if (time >= _weaponCooldownUntilMs)
        {
            _repositionPoint = null;

            if (dist2d < _data.maxAttackRange && targetPerception.canSee)
            {
                // In range — fire. fireWeapon is currently a placeholder AIOutput
                // field; Mob doesn't consume it yet, but setting it here mirrors
                // the template so the hookup is trivial later.
                output.fireWeapon = 0;
                _weaponCooldownUntilMs = time + (ulong)(_data.attackCooldownSeconds * 1000f);
            }
            else if (dist2d < _data.approachRange)
            {
                // Close the distance toward the last known position. If we can
                // see the target, stop at desiredAttackRange so we don't shove
                // them; otherwise walk all the way to where we last saw them.
                output.pathTarget = targetPerception.lastKnownPosition;
                output.pathSuccessDistance = targetPerception.canSee ? _data.desiredAttackRange : 0.5f;
                output.speed = 1.0f;
            }
        }
        else
        {
            // On cooldown — reposition around the target to desiredAttackRange
            // along a random-ish angle so the mob doesn't just stand still
            // between swings.
            if (!_repositionPoint.HasValue)
            {
                float angleToTarget = Mathf.Atan2(diff.X, diff.Z);
                float offsetAngle = angleToTarget + (float)(GD.Randf() * Mathf.Pi - Mathf.Pi * 0.5);
                Vector3 backup = targetPerception.lastKnownPosition
                    - new Vector3(Mathf.Sin(offsetAngle), 0f, Mathf.Cos(offsetAngle)) * _data.desiredAttackRange;
                _repositionPoint = backup;
            }

            output.pathTarget = _repositionPoint.Value;
            output.pathSuccessDistance = 0.5f;
            output.speed = 1.0f;
        }

        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
