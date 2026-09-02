using Godot;

// How a body of a given profile may cross a height change between two
// surfaces. One classification, four answers, and it is THE answer — the
// router, the barriers that physically stop a body, and the mantle that
// deliberately crosses one all read this and nothing else.
//
// That mattered because the rule had been written seven times (the pathfinder's
// edge expansion, the string-pull, the wander filter, NavigationGoals, the
// mantle probe, the steering guard, and the barrier mesher's own voxel-space
// copy) against three separate authorities for the number. Seven copies of a
// rule with three copies of its constant is how "the router refuses the drop
// but the body walks off it anyway" happens without anything looking wrong.
public enum EStepClass
{
    // Ordinary locomotion crosses it and nothing needs to know: a curb up, a
    // curb down. The step-up assist owns the rise.
    Walk,
    // Crossable, but only by a deliberate traversal that owns the body for its
    // duration — the mantle. Never entered by walking into it.
    Mantle,
    // A drop the body survives but takes only on purpose. Routing it needs the
    // caller's allowFalling; walking into it by accident is what barriers stop.
    Fall,
    // Not crossable at all in this direction.
    Blocked,
}

public static class TraversalRule
{
    // Classify a move from a surface at `fromSurfaceY` to one at `toSurfaceY`.
    // Both are surface heights in the WalkabilityGrid convention (the Y of the
    // air voxel a body stands in), so a caller holding voxel indices can pass
    // them directly.
    public static EStepClass Classify(in TraversalProfile profile, float fromSurfaceY, float toSurfaceY)
    {
        // A climber skips the caps by definition, and a flier's route is not a
        // walk at all — the vertical bands model a body on its feet and would
        // strand either at a ledge it simply goes over.
        if (profile.CanClimb || profile.CanFly)
        {
            return EStepClass.Walk;
        }
        float dy = toSurfaceY - fromSurfaceY;
        // Voxel surfaces are whole numbers apart; the epsilon is only against
        // float error in a caller that measured from a body's position.
        const float Epsilon = 0.001f;
        if (dy > Epsilon)
        {
            if (dy <= profile.strideHeight + Epsilon)
            {
                return EStepClass.Walk;
            }
            return dy <= profile.maxStepHeight + Epsilon ? EStepClass.Mantle : EStepClass.Blocked;
        }
        float drop = -dy;
        if (drop <= profile.strideHeight + Epsilon)
        {
            return EStepClass.Walk;
        }
        if (drop <= profile.maxStepHeight + Epsilon)
        {
            return EStepClass.Mantle;
        }
        return drop <= profile.maxFallHeight + Epsilon ? EStepClass.Fall : EStepClass.Blocked;
    }

    // Whether a route may step INTO water. Water is the one destination the
    // bands don't describe: entering it is never a climb (the body swims up to
    // the surface) and falling in is a splash, not a landing — so only the fall
    // depth applies, and a climber or flier ignores even that.
    public static bool CanEnterWater(in TraversalProfile profile, float fromSurfaceY, float toSurfaceY)
    {
        if (profile.CanClimb || profile.CanFly)
        {
            return true;
        }
        float drop = fromSurfaceY - toSurfaceY;
        return drop <= profile.maxFallHeight + 0.001f;
    }

    // Whether a ROUTE may cross this step. `allowFalling` is the caller's
    // intent, not a property of the body — wander passes false and a chase
    // passes true — which is precisely the part geometry cannot enforce and the
    // reason the router keeps a say at all.
    public static bool CanRoute(in TraversalProfile profile, float fromSurfaceY, float toSurfaceY, bool allowFalling)
    {
        EStepClass step = Classify(profile, fromSurfaceY, toSurfaceY);
        return step switch
        {
            EStepClass.Blocked => false,
            EStepClass.Fall => allowFalling,
            _ => true,
        };
    }
}
