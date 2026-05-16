using Godot;

// One-shot footprint decal spawner for Player / Mob. Stateless — callers
// decide when to fire (an animation frame match drives the timing), this
// just looks up the per-ground tint on SimData, bakes the alpha multiplier
// in, and delegates to World.SpawnFootprint. Surfaces with no FootprintColors
// entry are no-emit.
public static class FootprintEmitter
{
    public static void Emit(
        World world,
        Vector3 worldPos,
        float yaw,
        EGroundType ground,
        Texture2D texture,
        Vector2 size,
        float alphaMultiplier,
        float durationMultiplier,
        bool gated)
    {
        if (world == null || texture == null)
        {
            return;
        }
        SimData sim = world.SimData;
        if (sim?.FootprintColors == null)
        {
            return;
        }
        if (!sim.FootprintColors.TryGetValue(ground, out Color tint))
        {
            return;
        }
        Color spawnTint = new(tint.R, tint.G, tint.B, Mathf.Clamp(tint.A * alphaMultiplier, 0f, 1f));
        float duration = sim.FootprintDurationSeconds * durationMultiplier;
        world.SpawnFootprint(texture, size, spawnTint, worldPos, yaw, duration, gated);
    }
}
