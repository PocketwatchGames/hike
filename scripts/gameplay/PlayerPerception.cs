using Godot;

// Per-target awareness state for things the player can notice (mobs, spike
// traps, secret passages, ...). Hidden→Detected→Discovered is monotonic
// inside the helper; callers that want to fade Discovered→Hidden (mobs do,
// after a memory window) reset state.state externally.
public struct PerceivedByPlayerState
{
    public float perception;
    public EPlayerPerceptionState state;
    // Seeded with a random fraction of TickInterval at construction so
    // staggered spawns don't all raycast on the same physics frame.
    public float tickAccumulator;
}

// Per-target inputs passed to PlayerPerception.Tick. The base reach is the
// player's own `pd.visionRange`; everything in here is per-target tuning
// of how that reach is shaped against this specific percept.
public struct PerceptionInputs
{
    // Free positive scalar on the visibility distance. Bumps `pd.visionRange`
    // for large or conspicuous targets (chests, big secret doors, bosses)
    // so they cross the perception threshold from farther away. Default 1
    // leaves the curve as-authored. Doubles as the outer perf gate — the
    // helper short-circuits beyond `pd.visionRange * prominence`.
    public float prominence;
    // Per-target threshold for entering the Detected state from Hidden.
    // Set equal to discoveredThreshold to skip the Detected phase entirely
    // (chests pop straight from Hidden to Discovered with no suspicious
    // beat); set lower to give the target a long suspicious window
    // (traps, secret passages).
    public float detectedThreshold;
    // Per-target threshold for entering the Discovered state. Almost
    // always 1.0 (perception saturates there); lower values let
    // unmissable targets pop to Discovered before perception fully
    // fills.
    public float discoveredThreshold;
    // Height above the target's origin at which to sample world light.
    // Mobs sample at eye-level (~1m); a floor trap samples just above the
    // floor voxel so the value isn't zeroed by the block it sits in.
    public float lightSampleHeight;
    // Height above the target's origin used as the LOS raycast endpoint.
    // Mobs use eye-height (1.5m) so a doorway with a low lintel still
    // breaks LOS; floor targets use ~0 so a wall in front of the trap
    // blocks perception correctly.
    public float losRayHeight;
}

public struct PerceptionTickResult
{
    // True when state.state changed this tick (Hidden→Detected, etc.).
    public bool stateChanged;
    // True when perception accumulated this tick (player has visual contact
    // through LOS, light, and distance gates). Mobs use this to refresh
    // their MemoryTimeMs / VisibleTimeMs windows.
    public bool activelyPerceived;
}

public static class PlayerPerception
{
    public const float TickInterval = 0.1f;

    // Eye-height for the LOS raycast on the player's side. Matches the
    // value MobAI used so behavior is identical post-refactor.
    private const float PlayerEyeHeight = 1.5f;

    public static PerceptionTickResult Tick(
        World world,
        Vector3 targetPos,
        in PerceptionInputs inputs,
        ref PerceivedByPlayerState state,
        float delta)
    {
        var result = new PerceptionTickResult();
        if (world == null || world.player == null)
        {
            return result;
        }
        // Note: we don't early-return when state is Discovered. Mobs need
        // result.activelyPerceived to keep refreshing every tick they're
        // in sight — that flag drives MobSimState.VisibleTimeMs, which
        // gates the silhouette/memory visual. Callers that don't need
        // post-Discovered ticks (Discoverable) short-circuit themselves
        // before calling this. State transitions below are write-once
        // monotonic, so re-running the math on a Discovered target is
        // harmless.
        Player player = world.player;
        PlayerData pd = player.data;
        if (pd == null)
        {
            return result;
        }
        float targetLightMax = world.SimData?.TargetLightMax ?? 0.75f;

        EPlayerPerceptionState prevState = state.state;
        Vector3 toTarget = targetPos - player.GlobalPosition;
        float distSq = toTarget.LengthSquared();
        float perceptionDelta = 0f;

        // Maximum distance at which this target can ever register: the
        // player's own vision range scaled by the target's prominence.
        // Doubles as the outer perf gate — beyond this the curve already
        // returns 0 anyway.
        float maxVisibilityDistance = pd.visionRange * inputs.prominence;

        if (maxVisibilityDistance > 0f && distSq < maxVisibilityDistance * maxVisibilityDistance)
        {
            float lightAtTarget = world.GetPerceivedLight(targetPos + new Vector3(0f, inputs.lightSampleHeight, 0f));
            float lightFactor = targetLightMax > 0f ? Mathf.Clamp(lightAtTarget / targetLightMax, 0f, 1f) : 0f;
            float visibilityDistance = maxVisibilityDistance * lightFactor;
            if (visibilityDistance > 0f)
            {
                perceptionDelta = Mathf.Pow(
                    Mathf.Clamp(1f - (distSq / (visibilityDistance * visibilityDistance)), 0f, 1f),
                    pd.VisionDistancePower);
                if (perceptionDelta > pd.perceptionMinimum)
                {
                    Vector3 rayStart = targetPos + new Vector3(0f, inputs.losRayHeight, 0f);
                    Vector3 rayEnd = player.GlobalPosition + new Vector3(0f, PlayerEyeHeight, 0f);
                    using var query = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd, (uint)ECollisionLayer.Environment);
                    query.CollideWithAreas = false;
                    query.CollideWithBodies = true;
                    Godot.Collections.Dictionary rayResult = player.GetWorld3D().DirectSpaceState.IntersectRay(query);
                    if (rayResult.Count > 0)
                    {
                        perceptionDelta = 0f;
                    }
                }
                else
                {
                    perceptionDelta = 0f;
                }
            }
        }

        if (perceptionDelta > 0f)
        {
            result.activelyPerceived = true;
            if (perceptionDelta >= pd.perceptionInstant)
            {
                state.perception = 1f;
            }
            else
            {
                state.perception = Mathf.Min(1f, state.perception + perceptionDelta * delta * pd.PerceptionIncreaseSpeed);
            }
        }
        else
        {
            state.perception = Mathf.Max(0f, state.perception - pd.PerceptionRelaxationSpeed * delta);
        }

        if (state.perception >= inputs.discoveredThreshold)
        {
            state.state = EPlayerPerceptionState.Discovered;
        }
        else if (state.perception >= inputs.detectedThreshold && state.state == EPlayerPerceptionState.Hidden)
        {
            state.state = EPlayerPerceptionState.Detected;
        }

        result.stateChanged = state.state != prevState;
        return result;
    }

    // Force-promote a target to Discovered. Used when a trap triggers — the
    // player necessarily learns about it the moment the spikes pop out, even
    // if they hadn't accumulated enough perception to hit the threshold.
    public static void ForceDiscover(ref PerceivedByPlayerState state)
    {
        state.perception = 1f;
        state.state = EPlayerPerceptionState.Discovered;
    }
}
