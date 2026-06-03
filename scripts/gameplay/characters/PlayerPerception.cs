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
    // Skip the per-tick LOS raycast and treat the target as in sight whenever
    // the distance + light gates pass. Use for ground decals where the light
    // gate already encodes "behind a wall" (a wall blocks the light that
    // would reach the decal) and the per-target raycast cost dominates —
    // hundreds of footprints from multiple mobs would otherwise raycast every
    // perception tick.
    public bool skipLineOfSight;
    // When true, the LOS raycast ignores porous props (trees, foliage) and is
    // blocked only by solid terrain/walls. Set for flying / perched mobs so a
    // bird sitting in or flying through a canopy stays visible to the player
    // instead of being hidden by the very foliage it's on. Grounded targets
    // leave this false so a deer behind a tree is properly occluded.
    public bool seeThroughPorous;
    // Current sound output of the target in decibels. 0 = silent (no hearing
    // contribution). Mobs feed in their speed-mapped movement noise; static
    // discoverables leave this at 0.
    public float decibels;
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
        float delta,
        out PerceptionDebug debug)
    {
        var result = new PerceptionTickResult();
        debug = default;
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
        float visionDelta = 0f;

        // Maximum distance at which this target can ever register: the
        // player's own vision range scaled by the target's prominence.
        // Doubles as the outer perf gate — beyond this the curve already
        // returns 0 anyway.
        float maxVisibilityDistance = pd.visionRange * inputs.prominence;

        // Player has no facing-based gating on perception (the camera frames
        // the player's view independent of body orientation), so facing is
        // always 1 for the debug breakdown.
        debug.facing = 1f;
        debug.distance = maxVisibilityDistance > 0f
            ? Mathf.Clamp(1f - Mathf.Sqrt(distSq) / maxVisibilityDistance, 0f, 1f)
            : 0f;

        if (maxVisibilityDistance > 0f && distSq < maxVisibilityDistance * maxVisibilityDistance)
        {
            float lightAtTarget = world.GetPerceivedLight(targetPos + new Vector3(0f, inputs.lightSampleHeight, 0f));
            float lightFactor = targetLightMax > 0f ? Mathf.Clamp(lightAtTarget / targetLightMax, 0f, 1f) : 0f;
            // Night vision lifts the darkness-suppression term toward full
            // brightness: relief is the fraction of the darkness penalty the
            // player's equipment removes (e.g. a NightVision modifier of 1.85
            // yields 0.85, so darkness only costs 15% of what it normally would).
            float nightVisionRelief = Mathf.Clamp(player.ComposeStat(EStat.NightVision) - 1f, 0f, 1f);
            if (nightVisionRelief > 0f)
            {
                lightFactor = Mathf.Lerp(lightFactor, 1f, nightVisionRelief);
            }
            debug.lighting = lightFactor;
            // Fog/rain shorten sight; sampled at the player (the perceiver here).
            float visibilityDistance = maxVisibilityDistance * lightFactor * VisionRangeMultiplier(world, player.GlobalPosition);
            if (visibilityDistance > 0f)
            {
                visionDelta = Mathf.Pow(
                    Mathf.Clamp(1f - (distSq / (visibilityDistance * visibilityDistance)), 0f, 1f),
                    pd.VisionRangePower);
                if (visionDelta > pd.perceptionMinimum)
                {
                    if (!inputs.skipLineOfSight)
                    {
                        Vector3 rayStart = targetPos + new Vector3(0f, inputs.losRayHeight, 0f);
                        Vector3 rayEnd = player.GlobalPosition + new Vector3(0f, PlayerEyeHeight, 0f);
                        uint losMask = inputs.seeThroughPorous ? (uint)ECollisionLayer.Environment : (uint)ECollisionLayer.Solid;
                        using var query = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd, losMask);
                        query.CollideWithAreas = false;
                        query.CollideWithBodies = true;
                        Godot.Collections.Dictionary rayResult = player.GetWorld3D().DirectSpaceState.IntersectRay(query);
                        if (rayResult.Count > 0)
                        {
                            visionDelta = 0f;
                        }
                    }
                }
                else
                {
                    visionDelta = 0f;
                }
            }
        }

        // Hearing contribution. Audible distance = decibels * hearingRange;
        // inside that radius the delta ramps linearly from 0 at the edge to
        // 1 at the source. No LOS / light gate — sound rounds corners and
        // works in the dark, which is the whole point of pairing it with
        // vision.
        float hearingDelta = 0f;
        if (inputs.decibels > 0f && pd.hearingRange > 0f)
        {
            // Wind masks sound, fog carries it — sampled at the listener (player).
            float maxAudibleDistance = inputs.decibels * pd.hearingRange
                * HearingRangeMultiplier(world, player.GlobalPosition);
            if (distSq < maxAudibleDistance * maxAudibleDistance)
            {
                hearingDelta = Mathf.Pow(1f - Mathf.Sqrt(distSq) / maxAudibleDistance, pd.hearingRangePower);
            }
        }

        debug.vision = visionDelta;
        debug.hearing = hearingDelta;
        // Player doesn't smell — leave debug.smell at 0.
        // visionDelta is non-zero only when range, light, and LOS (or skip)
        // all passed — equivalent to "the player can see the target right now".
        debug.los = visionDelta > 0f;

        float visionContribution = visionDelta * pd.VisionStrength;
        float hearingContribution = hearingDelta * pd.HearingStrength;
        float totalContribution = visionContribution + hearingContribution;

        bool visuallyPerceived = visionContribution > 0f;
        if (totalContribution > 0f)
        {
            // activelyPerceived drives mob memory refresh (VisibleTimeMs) —
            // it must mean "the mob is in active visual contact right now",
            // not "I can also hear footsteps from around the corner". Keep
            // it tied to vision only.
            result.activelyPerceived = visuallyPerceived;
            if (visionContribution >= pd.perceptionInstant)
            {
                state.perception = 1f;
            }
            else
            {
                state.perception = Mathf.Min(1f, state.perception + totalContribution * delta * pd.PerceptionIncreaseSpeed);
            }
        }
        else
        {
            state.perception = Mathf.Max(0f, state.perception - pd.PerceptionRelaxationSpeed * delta);
        }

        // State transitions require active visual contact. Hearing builds
        // the perception meter but can't latch the player into Detected /
        // Discovered on its own — they have to see the target this tick.
        if (visuallyPerceived)
        {
            if (state.perception >= inputs.discoveredThreshold)
            {
                state.state = EPlayerPerceptionState.Discovered;
            }
            else if (state.perception >= inputs.detectedThreshold && state.state == EPlayerPerceptionState.Hidden)
            {
                state.state = EPlayerPerceptionState.Detected;
            }
        }

        result.stateChanged = state.state != prevState;
        return result;
    }

    // Speed → continuous-noise decibel mapping shared by Player and Mob.
    // Piecewise linear: 0 at rest, sneakDecibels at sneakSpeed, runDecibels
    // at runSpeed (and capped at runDecibels above that). Stationary actors
    // emit 0 so a frozen mob is acoustically invisible.
    public static float ComputeMovementDecibels(float speed, float sneakSpeed, float runSpeed, float sneakDecibels, float runDecibels)
    {
        if (speed <= 0.001f)
        {
            return 0f;
        }
        if (speed >= runSpeed)
        {
            return runDecibels;
        }
        if (speed >= sneakSpeed)
        {
            float t = (speed - sneakSpeed) / Mathf.Max(0.001f, runSpeed - sneakSpeed);
            return Mathf.Lerp(sneakDecibels, runDecibels, t);
        }
        return sneakDecibels * (speed / Mathf.Max(0.001f, sneakSpeed));
    }

    // ===== Environmental sense modifiers =====
    // Wind, fog, and rain shape every sense the same way regardless of who is
    // perceiving, so both perception paths (this Tick for player→mob, and
    // MobAI.UpdatePerception for mob→player) route through these helpers.
    // Each returns a multiplier on a sense's range (~1 in calm, clear air).

    // Normalized wind strength [0,1] at a world position: SampleWindSpeed
    // (which already zeroes out underground / under cover) over the authored
    // PerceptionWindReference, clamped. Public so MobAI can sample it once
    // for the per-crumb smell directionality instead of per crumb.
    public static float WindFraction(World world, Vector3 pos)
    {
        SimData sim = world?.SimData;
        if (sim == null)
        {
            return 0f;
        }
        GameClient gc = GameClient.Current;
        float windSpeed = gc != null ? gc.SampleWindSpeed(pos) : 0f;
        if (windSpeed <= 0f)
        {
            return 0f;
        }
        return Mathf.Clamp(windSpeed / Mathf.Max(0.001f, sim.PerceptionWindReference), 0f, 1f);
    }

    // Normalized fog density [0,1] at a world position. Single-voxel sample
    // (same as AmbienceController) — fog is regionally smooth, so trilinear
    // filtering would buy nothing here.
    private static float FogFraction(World world, Vector3 pos)
    {
        WorldState ws = world?.WorldState;
        if (ws == null)
        {
            return 0f;
        }
        return ws.GetFogWorld(Mathf.FloorToInt(pos.X), Mathf.FloorToInt(pos.Y), Mathf.FloorToInt(pos.Z)) / 255f;
    }

    // Vision-range multiplier at `samplePos`: fog (dominant) and rain both add
    // haze that shortens sight. Both perception paths sample the fog at the
    // PLAYER's position — fog around the player is what reads on screen, and
    // the player is the perceiver (player→mob) or the target (mob→player) in
    // each case. Rain is the global blended amount.
    public static float VisionRangeMultiplier(World world, Vector3 samplePos)
    {
        SimData sim = world?.SimData;
        if (sim == null)
        {
            return 1f;
        }
        float fog = FogFraction(world, samplePos);
        float rain = world.CurrentRainAmount();
        return Mathf.Max(0f, (1f - sim.FogVisionReduction * fog) * (1f - sim.RainVisionReduction * rain));
    }

    // Hearing-range multiplier at the listener: wind masks sound (turbulent
    // air scatters it) while still, damp fog carries it farther.
    public static float HearingRangeMultiplier(World world, Vector3 listenerPos)
    {
        SimData sim = world?.SimData;
        if (sim == null)
        {
            return 1f;
        }
        float wind = WindFraction(world, listenerPos);
        float fog = FogFraction(world, listenerPos);
        return Mathf.Max(0f, (1f - sim.HearingWindSuppression * wind) * (1f + sim.FogHearingBoost * fog));
    }

    // Non-directional smell-range multiplier at the nose: humid fog holds
    // scent (widens reach) while high wind scatters it (shrinks reach). The
    // downwind/upwind directional term is per-source and applied by MobAI.
    public static float SmellRangeMultiplier(World world, Vector3 nosePos)
    {
        SimData sim = world?.SimData;
        if (sim == null)
        {
            return 1f;
        }
        float wind = WindFraction(world, nosePos);
        float fog = FogFraction(world, nosePos);
        return Mathf.Max(0f, (1f - sim.SmellWindDisruption * wind) * (1f + sim.FogSmellBoost * fog));
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
