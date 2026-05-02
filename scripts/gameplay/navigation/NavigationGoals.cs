using Godot;

// Static helpers that produce world-space goal points for behaviors to
// hand to MobNavigator.Goto. Separated from MobNavigator so behaviors
// can mix goal kinds (standoff, cover, retreat) without bloating the
// navigator's API surface.
public static class NavigationGoals
{
    private const float StandoffEyeHeight = 1.5f;

    // Resolve a slot on a target's encircle ring to a concrete world
    // position the mob should stand at. Tries the slot's exact angle
    // first; if that point isn't reachable (LOS blocked, off the
    // streamed window, sitting on a wall) sweeps ±N degrees in
    // increasing offsets until it finds a reachable candidate or
    // exhausts attempts. Falls back to the nominal point on failure —
    // the navigator's A* layer will fail-soft from there.
    //
    // requireLineOfSight=true matches the typical "ranged mob holds
    // position with a clear shot" use case. Pass false for melee
    // standoff where line-of-sight isn't required at the slot itself.
    public static Vector3 PickStandoffPoint(
        World world,
        Vector3 targetPos,
        float distance,
        float slotAngle,
        bool requireLineOfSight = true,
        int sweepAttempts = 5,
        float sweepStepDegrees = 22.5f)
    {
        if (world == null || distance <= 0f)
        {
            return targetPos;
        }
        WorldState ws = world.WorldState;
        if (ws == null)
        {
            return targetPos;
        }

        // Sweep order: 0, +1, -1, +2, -2, ... so we try the requested
        // angle first then alternate on either side at increasing offsets.
        // First reachable candidate wins.
        float stepRad = Mathf.DegToRad(sweepStepDegrees);
        for (int attempt = 0; attempt <= sweepAttempts * 2; attempt++)
        {
            int absOffset = (attempt + 1) / 2;
            int sign = (attempt == 0) ? 0 : ((attempt % 2 == 1) ? 1 : -1);
            int offsetSlots = absOffset * sign;
            float angle = slotAngle + offsetSlots * stepRad;
            Vector3 candidate = targetPos + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * distance;

            if (!IsStandable(ws, world, candidate, out Vector3 surfacePoint))
            {
                continue;
            }
            if (requireLineOfSight && !HasLineOfSight(world, surfacePoint, targetPos))
            {
                continue;
            }
            return surfacePoint;
        }

        // No candidate worked — return the nominal position. The caller's
        // navigator will route as close as it can and may end up flagged
        // blocked, which BehaviorAttack can act on.
        return targetPos + new Vector3(Mathf.Sin(slotAngle), 0f, Mathf.Cos(slotAngle)) * distance;
    }

    // True if `worldPos` sits over a column with a standable surface
    // within a couple voxels of the requested Y. Snaps the returned
    // `surfacePoint` to that surface so callers can hand the navigator a
    // point that's actually on the ground rather than mid-air.
    private static bool IsStandable(WorldState ws, World world, Vector3 worldPos, out Vector3 surfacePoint)
    {
        surfacePoint = worldPos;
        int wx = Mathf.FloorToInt(worldPos.X);
        int wz = Mathf.FloorToInt(worldPos.Z);
        int anchorY = Mathf.FloorToInt(worldPos.Y);
        // Search ±SurfaceSearchRadius around the anchor Y for a standable
        // air voxel. Mirrors WalkabilityGrid.SampleColumn's contract for
        // ground mobs (no climber / swimmer profile here — standoff
        // points should be solid ground).
        for (int dy = WalkabilityGrid.SurfaceSearchRadius; dy >= -WalkabilityGrid.SurfaceSearchRadius; dy--)
        {
            int wy = anchorY + dy;
            if (!ws.IsInBounds(wx, wy, wz))
            {
                return false;
            }
            VoxelType here = ws.GetVoxelWorld(wx, wy, wz);
            if (VoxelTypeInfo.IsSolid(here))
            {
                continue;
            }
            if (!ws.IsInBounds(wx, wy - 1, wz))
            {
                return false;
            }
            if (!VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(wx, wy - 1, wz)))
            {
                continue;
            }
            // 2-voxel headroom check so the standoff point is in a slot
            // the mob actually fits into.
            if (!ws.IsInBounds(wx, wy + 1, wz) || VoxelTypeInfo.IsSolid(ws.GetVoxelWorld(wx, wy + 1, wz)))
            {
                continue;
            }
            // Mirror WalkabilityGrid's entity-blocker rejection so standoff
            // slots can't land on a cell occupied by a tree or chest.
            if (world != null && (world.IsPathBlocked(wx, wy, wz) || world.IsPathBlocked(wx, wy + 1, wz)))
            {
                continue;
            }
            surfacePoint = new Vector3(wx + 0.5f, wy, wz + 0.5f);
            return true;
        }
        return false;
    }

    // Environment-only line-of-sight raycast at eye height. Mirrors the
    // LOS check in BehaviorInvestigate / UpdatePerception so all three
    // systems agree on what counts as visible.
    private static bool HasLineOfSight(World world, Vector3 from, Vector3 to)
    {
        Vector3 a = from + new Vector3(0f, StandoffEyeHeight, 0f);
        Vector3 b = to + new Vector3(0f, StandoffEyeHeight, 0f);
        var query = PhysicsRayQueryParameters3D.Create(a, b, (uint)ECollisionLayer.Environment);
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        var result = world.GetWorld3D().DirectSpaceState.IntersectRay(query);
        return result.Count == 0;
    }
}
