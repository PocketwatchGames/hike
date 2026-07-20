using Godot;

// One-shot footprint decal spawner for Player / Mob. Stateless — callers
// decide when to fire (an animation frame match drives the timing), this
// just looks up the per-ground tint on SimData, bakes the alpha multiplier
// in, and delegates to Sim.SpawnFootprint. Surfaces with no FootprintColors
// entry are no-emit.
public static class FootprintEmitter
{
    public static void Emit(
        Sim sim,
        Vector3 worldPos,
        float yaw,
        EGroundType ground,
        Texture2D texture,
        Vector2 size,
        float alphaMultiplier,
        float durationMultiplier,
        bool gated)
    {
        if (sim == null || texture == null)
        {
            return;
        }
        SimData simData = sim.SimData;
        if (simData?.footprintColors == null)
        {
            return;
        }
        if (!simData.footprintColors.TryGetValue(ground, out Color tint))
        {
            return;
        }
        Color spawnTint = new(tint.R, tint.G, tint.B, Mathf.Clamp(tint.A * alphaMultiplier, 0f, 1f));
        float duration = simData.footprintDurationSeconds * durationMultiplier;
        sim.SpawnFootprint(texture, size, spawnTint, worldPos, yaw, duration, gated);
    }
}
