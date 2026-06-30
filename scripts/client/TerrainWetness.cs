using Godot;
using System;

// CPU mirror of the standing-water (puddle) field that voxel_clip.gdshader draws,
// so gameplay can ask "is this world spot in a puddle?" and agree with what's on
// screen. The footstep system uses it to swap a dry step for a splash.
//
// SkyController.Apply pushes the tuning fields here every frame — the SAME values
// it sends to the shader globals — and WetnessLevel comes from WorldState. The
// noise (Hash13 / Vnoise) is a 1:1 port of the shader functions, so the puddle
// SHAPE matches. The two GPU-only biases the shader folds into coverage — per-tile
// height-map micro-pits (a texture sample) and the per-vertex concavity bake (mesh
// data) — are intentionally dropped here: the noise field + weather coverage are
// what dominate the look, and reproducing those biases on the CPU would mean
// duplicating the atlas sample and the DC mesher. Result: the CPU agrees with the
// rendered puddles to within the rim, which is all a footstep trigger needs.
public static class TerrainWetness
{
    // Mirror of the voxel_clip puddle globals — set by SkyController.Apply each
    // frame so a query here matches the frame's rendered puddles. Defaults match
    // the SkyController export defaults / project.godot shader-global values.
    public static float PuddleScale = 0.3f;
    public static float PuddleEdge = 0.08f;
    public static float PuddleRamp = 0.6f;
    public static float PoolStrength = 0.85f;
    public static float PoolFlatness = 0.96f;

    // Standing-water depth at/over which a footstep counts as "in a puddle" and
    // splashes. The shader fades pools in continuously (no hard cutoff); this is
    // purely the gameplay trigger point — a thin rim shouldn't splash.
    public const float PuddleStepThreshold = 0.35f;
    // Below this sky exposure [0,1] the spot is considered sheltered (cave / under
    // cover) and never pools — the CPU analog of the shader's sun_mask gate.
    private const float MinSkyExposure = 0.5f;

    // Standing-water depth [0,1] at a world XZ for the given weather wetness — the
    // CPU analog of the shader's pool_shape (noise thresholded by coverage), before
    // the slope / sky gates (callers apply those from gameplay state).
    public static float PuddleField(float wetnessLevel, float worldX, float worldZ)
    {
        float w = Mathf.Clamp(wetnessLevel, 0f, 1f);
        float coverage = Mathf.Clamp(MathF.Pow(w, PuddleRamp) * PoolStrength, 0f, 1f);
        if (coverage <= 0f)
        {
            return 0f;
        }
        // World XZ → noise space, matching `puddle_uv = world.xz * puddle_scale`.
        float ux = worldX * PuddleScale;
        float uz = worldZ * PuddleScale;
        float field = Vnoise(ux, uz, 0f) * 0.65f + Vnoise(ux * 2.7f, uz * 2.7f, 19f) * 0.35f;
        float thresh = 1f - coverage;
        return Smoothstep(thresh, thresh + PuddleEdge, field);
    }

    // True when `worldPos` is a steppable puddle: enough standing water, on
    // near-flat ground (floorNormalY ≥ PoolFlatness), under open enough sky.
    // Mirrors the shader's pool_flat + sun_mask gates so a splash only fires
    // where a puddle is actually drawn.
    public static bool IsPuddleStep(WorldState ws, Vector3 worldPos, float floorNormalY)
    {
        if (ws == null || floorNormalY < PoolFlatness)
        {
            return false;
        }
        if (ws.GetSkyExposure01(worldPos) < MinSkyExposure)
        {
            return false;
        }
        return PuddleField(ws.WetnessLevel, worldPos.X, worldPos.Z) >= PuddleStepThreshold;
    }

    // --- 1:1 ports of voxel_clip.gdshader hash13 / vnoise (value noise) ---

    private static float Fract(float x)
    {
        return x - MathF.Floor(x);
    }

    private static float Hash13(float px, float py, float pz)
    {
        px = Fract(px * 0.3183099f + 0.1f);
        py = Fract(py * 0.3183099f + 0.1f);
        pz = Fract(pz * 0.3183099f + 0.1f);
        px *= 17f;
        py *= 17f;
        pz *= 17f;
        return Fract(px * py * pz * (px + py + pz));
    }

    private static float Vnoise(float x, float y, float z)
    {
        float ix = MathF.Floor(x);
        float iy = MathF.Floor(y);
        float iz = MathF.Floor(z);
        float fx = x - ix;
        float fy = y - iy;
        float fz = z - iz;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        fz = fz * fz * (3f - 2f * fz);
        float n000 = Hash13(ix, iy, iz);
        float n100 = Hash13(ix + 1f, iy, iz);
        float n010 = Hash13(ix, iy + 1f, iz);
        float n110 = Hash13(ix + 1f, iy + 1f, iz);
        float n001 = Hash13(ix, iy, iz + 1f);
        float n101 = Hash13(ix + 1f, iy, iz + 1f);
        float n011 = Hash13(ix, iy + 1f, iz + 1f);
        float n111 = Hash13(ix + 1f, iy + 1f, iz + 1f);
        float nx00 = Mathf.Lerp(n000, n100, fx);
        float nx10 = Mathf.Lerp(n010, n110, fx);
        float nx01 = Mathf.Lerp(n001, n101, fx);
        float nx11 = Mathf.Lerp(n011, n111, fx);
        float nxy0 = Mathf.Lerp(nx00, nx10, fy);
        float nxy1 = Mathf.Lerp(nx01, nx11, fy);
        return Mathf.Lerp(nxy0, nxy1, fz);
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Mathf.Clamp((x - edge0) / MathF.Max(edge1 - edge0, 1e-5f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
