using Godot;

// Aerial combatant: runs the inherited encircle / approach / weapon logic, then
// layers flight on top. The mob stays airborne for the entire engagement and
// moves between two altitude tiers chosen from the weapon it's bringing to bear:
//
//   * Encircle / melee tier — hovers at the target's own elevation (the physics
//     layer floors this ~1m above the ground), so it circles at the player's
//     height and darts in level for a melee strike.
//   * Ranged tier — rises to a fixed height above the terrain to fire its
//     long-range attack down at the target.
//
// The horizontal encircle ring, slot leasing, weapon selection and facing all
// come from BehaviorAttack unchanged; this class only decides the height.
public partial class BehaviorFlyAttack : BehaviorAttack
{
    private readonly FlyAttackBehaviorData _flyData;

    // Latched high (ranged) vs low (encircle/melee) tier. Held steady while a
    // locks-movement attack owns the body so cooldowns flipping the chosen
    // weapon mid-swing can't bob the mob between heights; re-evaluated each tick
    // the body is free.
    private bool _highTier;

    public BehaviorFlyAttack(FlyAttackBehaviorData data) : base(data)
    {
        _flyData = data;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        BehaviorOutput result = base.Run(me, time, ref targetPerception, ref output);
        // Only layer flight onto an active engagement. On a transition tick the
        // next behavior owns the airborne flag.
        if (result.result != EBehaviorResult.Running)
        {
            return result;
        }

        // Stay aloft for the whole engagement — including a melee dart's locked
        // windup/recovery, where the physics layer keeps gravity off via the
        // broader airborne-intent flag so the mob doesn't drop out of the sky.
        output.airborne = true;

        // No engaged target this tick (lost sight between ticks): hold the
        // default cruise hover until the behavior transitions out. The base sets
        // the Attacking bit only when it has a real target.
        if ((output.behaviorFlags & EBehaviorFlags.Attacking) == 0)
        {
            return result;
        }

        // Pick the tier from the weapon being brought to bear, but freeze it
        // while an action owns the body so the mob commits to the height it
        // began the swing at.
        bool locked = me.Runner != null && me.Runner.LocksMovement;
        if (!locked)
        {
            WeaponData engaging = ChosenWeapon;
            _highTier = engaging != null && engaging.desiredAttackRange >= _flyData.rangedTierRange;
        }

        if (_highTier)
        {
            output.flyAltitude = _flyData.rangedAltitude;
            output.flyTargetY = null;
        }
        else
        {
            output.flyTargetY = output.targetPos.Y + _flyData.engageHeightOffset;
            output.flyAltitude = null;
        }
        return result;
    }
}
