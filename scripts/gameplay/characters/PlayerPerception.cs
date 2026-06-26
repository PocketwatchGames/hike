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
    // How conspicuous this target is — a free positive scalar on perception
    // CLARITY (not range). Higher clears the perception floor sooner, so the
    // target resolves faster and (via the floor) from farther; lower means the
    // player must get closer or stare longer. The mob caller folds its
    // transient visibility (movement / camouflage / airborne / perched) into
    // this. 1 = neutral.
    public float prominence;
    // Multiplier on the player's hard sightline cap (`pd.visionRange`). Almost
    // always 1; only a genuinely huge target that must register beyond normal
    // vision range passes >1. Doubles as the cheap outer perf gate — the helper
    // short-circuits beyond `pd.visionRange * rangeScale` (closeness is 0 there,
    // so nothing registers anyway). MUST be set by every caller (struct default
    // 0 would make the target invisible).
    public float rangeScale;
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

    // Eye-height for the LOS raycast on the player's side. Matches the value
    // MobAI uses so player→mob and mob→player LOS behave identically.
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

        // Hard geometric sightline cap: the player's vision range, extended only
        // for the rare huge target via rangeScale. This is the ONLY thing the
        // target's size moves about range; everything else (light/fog/prominence)
        // shapes clarity, and the perception floor turns low clarity into a short
        // PRACTICAL range. The squared-distance compare is the cheap outer perf
        // gate — beyond the cap nothing registers, so we skip the light sample +
        // raycast.
        float maxRange = pd.visionRange * inputs.rangeScale;
        bool inRange = maxRange > 0f && distSq < maxRange * maxRange;

        // Player has no facing-based gating on perception (the camera frames
        // the player's view independent of body orientation), so facing is
        // always 1 for the debug breakdown.
        debug.facing = 1f;
        // Closeness ramps to 1 at the player, but its zero-crossing sits PAST the
        // cap (visionRangeCurveExtension), so at the edge of range closeness is
        // still (1 − 1/extension) rather than 0 — a target can cross instant the
        // moment it enters range in good light instead of fading in at the border.
        float closeness = inRange
            ? Mathf.Clamp(1f - Mathf.Sqrt(distSq) / (maxRange * pd.visionRangeCurveExtension), 0f, 1f)
            : 0f;
        debug.distance = closeness;

        if (inRange)
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
            // Dark-adaptation relief: a dilated pupil partially lifts the same
            // darkness-suppression term, scaled by eyeDilationVisionRelief so it's
            // only a partial help (and stacks on top of equipment night vision).
            // This is the gameplay half of EyeDilation — standing in the gloom long
            // enough lets the player notice nearby dim things a little better.
            float dilationRelief = player.EyeDilation * pd.eyeDilationVisionRelief;
            if (dilationRelief > 0f)
            {
                lightFactor = Mathf.Lerp(lightFactor, 1f, dilationRelief);
            }
            debug.lighting = lightFactor;
            // Clarity: how clearly the target reads right now. Darkness, fog, and
            // rain each obscure independently and multiply; the result times the
            // target's conspicuousness (movement / camouflage folded in by the
            // caller) is the signal. Compounding is intentional — in poor
            // conditions it sinks the build RATE and shortens practical range via
            // the floor below, without crushing the hard range, so distant targets
            // still register and grow slowly (uncertainty) rather than vanishing.
            //
            // clarityPower shapes how SOON each condition bites as it ramps in,
            // applied to the condition's own strength (darkness / fog / rain
            // density) — NOT to the final multiplier. That leaves each authored
            // SimData reduction meaning exactly what it says at full strength
            // (FogVisionReduction = 0.5 → a 0.5 multiplier in full fog), instead of
            // the old product-power that squared 0.5 into an effective 0.75
            // reduction. >1 = murkier sooner (a little fog/dusk already bites);
            // 1 = linear. Full darkness still drives clarity to 0 (lightFactor 0 →
            // mLight 0), preserving the zero-light invariant.
            SimData sim = world.SimData;
            // Fog obscures the whole sightline, so average its density at both ends
            // rather than only at the player — a mob standing in a low-lying fog
            // bank then reads as obscured even when the player is in clear air (and
            // vice-versa). Cheap (two single-voxel lookups) and symmetric, so a
            // sightline reads the same fog whichever way it's perceived.
            float fog = 0.5f * (FogFraction(world, player.GlobalPosition) + FogFraction(world, targetPos));
            float rain = world.CurrentRainAmount();
            float bite = 1f / Mathf.Max(0.01f, pd.clarityPower);
            float mLight = 1f - Mathf.Pow(1f - lightFactor, bite);
            float mFog = sim != null ? 1f - sim.FogVisionReduction * Mathf.Pow(fog, bite) : 1f;
            float mRain = sim != null ? 1f - sim.RainVisionReduction * Mathf.Pow(rain, bite) : 1f;
            float clarity = Mathf.Max(0f, mLight * mFog * mRain) * inputs.prominence;
            // Signal = closeness curve × clarity. perceptionMinimum is the floor:
            // below it the target can't register even with a perfectly clear line,
            // so we skip the raycast entirely (the big perf win) and leave LOS
            // Unchecked — we never looked. The floor also gates perception build
            // and the conspicuousness/range emergent behavior.
            visionDelta = Mathf.Pow(closeness, pd.VisionRangePower) * clarity;
            if (visionDelta > pd.perceptionMinimum)
            {
                bool blocked = false;
                if (!inputs.skipLineOfSight)
                {
                    Vector3 rayStart = targetPos + new Vector3(0f, inputs.losRayHeight, 0f);
                    Vector3 rayEnd = player.GlobalPosition + new Vector3(0f, PlayerEyeHeight, 0f);
                    uint losMask = inputs.seeThroughPorous ? (uint)ECollisionLayer.Environment : (uint)ECollisionLayer.Solid;
                    using var query = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd, losMask);
                    query.CollideWithAreas = false;
                    query.CollideWithBodies = true;
                    Godot.Collections.Dictionary rayResult = player.GetWorld3D().DirectSpaceState.IntersectRay(query);
                    blocked = rayResult.Count > 0;
                }
                if (blocked)
                {
                    visionDelta = 0f;
                    debug.los = EPerceptionLos.Blocked;
                }
                else
                {
                    debug.los = EPerceptionLos.Clear;
                }
            }
            else
            {
                // Below the floor — never looked. visionDelta stays the sub-floor
                // value; the accumulation block treats it as no contact. Leave
                // debug.los Unchecked (the default).
                visionDelta = 0f;
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
        // debug.los was set in the in-range block above (Clear/Blocked when the
        // raycast ran, left Unchecked when below the floor or out of range).

        float visionContribution = visionDelta * pd.VisionStrength;
        float hearingContribution = hearingDelta * pd.HearingStrength;
        // Combined sensory signal. It sets how FAST the meter fills, but the fill
        // time is fit to it (see the remap) — perceivability is never bent to hit
        // a target time.
        float perceivability = visionContribution + hearingContribution;

        // activelyPerceived drives mob memory refresh (VisibleTimeMs) — it must
        // mean "the mob is in active visual contact right now", not "I can also
        // hear footsteps from around the corner". Keep it tied to vision only.
        bool visuallyPerceived = visionContribution > 0f;
        if (visionContribution >= pd.perceptionInstant)
        {
            // Just below instant fills in minPerceptionFillSeconds; at/above it
            // there's nothing left to resolve, so it pops immediately.
            result.activelyPerceived = visuallyPerceived;
            state.perception = 1f;
        }
        else if (perceivability > pd.perceptionMinimum)
        {
            result.activelyPerceived = visuallyPerceived;
            // Map perceivability's position in (perceptionMinimum, perceptionInstant)
            // to a fill time: just above the floor → maxPerceptionFillSeconds (slowest
            // the meter ever moves), just under instant → minPerceptionFillSeconds.
            // Anything slower than the max is, by construction, below the floor and
            // falls to the decay branch — so the meter never crawls slower than max.
            float t = Mathf.Clamp(
                (perceivability - pd.perceptionMinimum) / Mathf.Max(0.0001f, pd.perceptionInstant - pd.perceptionMinimum),
                0f, 1f);
            float fillSeconds = Mathf.Lerp(pd.maxPerceptionFillSeconds, pd.minPerceptionFillSeconds, t);
            state.perception = Mathf.Min(1f, state.perception + delta / Mathf.Max(0.0001f, fillSeconds));
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
        float windSpeed = world.SampleWindSpeed(pos);
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

    // Vision-range multiplier along the sightline between `perceiverPos` and
    // `targetPos`: fog (dominant) and rain both add haze that shortens sight. Fog
    // is averaged at both ends so a target standing in a fog bank reads as
    // obscured even from clear air (and the value is symmetric — a sightline reads
    // the same fog whichever direction it's perceived). Rain is the global blended
    // amount. (player→mob applies its own clarityPower-shaped fog inline; this is
    // the linear mob→player / mob→mob path.)
    public static float VisionRangeMultiplier(World world, Vector3 perceiverPos, Vector3 targetPos)
    {
        SimData sim = world?.SimData;
        if (sim == null)
        {
            return 1f;
        }
        float fog = 0.5f * (FogFraction(world, perceiverPos) + FogFraction(world, targetPos));
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
