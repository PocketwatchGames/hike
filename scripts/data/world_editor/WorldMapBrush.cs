using Godot;

// Shared, layer-agnostic stamp engine. Holds the falloff/flow/noise tuning;
// each tool supplies its own radius (a per-tool variable) and a per-texel
// callback that does the actual layer write. Authorable as a .tres preset.
[GlobalClass]
public partial class WorldMapBrush : Resource
{
    // Inner fraction of the radius at full strength before the soft edge.
    [Export(PropertyHint.Range, "0,1,0.01")] public float hardness = 0.4f;

    // Per-application strength multiplier (0..1). Held drags apply per motion
    // event, so Flow controls how fast a stroke builds.
    [Export(PropertyHint.Range, "0,1,0.01")] public float flow = 0.5f;

    // Noise modulation amplitude + frequency (used by tools that opt in, e.g.
    // elevation raise/lower). Sub-0.01 frequencies are meaningful, hence the
    // fine range step (see the [Export]-precision note in CLAUDE.md).
    [Export(PropertyHint.Range, "0,1,0.01")] public float noiseAmount = 0f;
    [Export(PropertyHint.Range, "0.001,1,0.0001")] public float noiseFrequency = 0.05f;

    private FastNoiseLite _noise;

    // Signed noise contribution at a texel, scaled by NoiseAmount (0 if off).
    public float NoiseAt(int px, int pz)
    {
        if (noiseAmount <= 0f)
        {
            return 0f;
        }
        if (_noise == null)
        {
            _noise = new FastNoiseLite();
            _noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        }
        _noise.Frequency = noiseFrequency;
        return _noise.GetNoise2D(px, pz) * noiseAmount;
    }

    // Iterate texels within `radius` of `center`, invoking `apply(px, pz, weight)`
    // for each (weight in (0,1] from the falloff). Returns the clamped bounding
    // rect of visited texels so the caller can re-bake / re-colour that region.
    public Rect2I Stamp(Vector2I center, float radius, int imgW, int imgH, System.Action<int, int, float> apply)
    {
        int r = Mathf.CeilToInt(radius);
        int x0 = Mathf.Max(0, center.X - r);
        int x1 = Mathf.Min(imgW - 1, center.X + r);
        int z0 = Mathf.Max(0, center.Y - r);
        int z1 = Mathf.Min(imgH - 1, center.Y + r);
        if (x1 < x0 || z1 < z0)
        {
            return new Rect2I(0, 0, 0, 0);
        }

        for (int px = x0; px <= x1; px++)
        {
            for (int pz = z0; pz <= z1; pz++)
            {
                float dx = px - center.X;
                float dz = pz - center.Y;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > radius)
                {
                    continue;
                }
                float weight = Falloff(dist / radius);
                if (weight <= 0f)
                {
                    continue;
                }
                apply(px, pz, weight);
            }
        }
        return new Rect2I(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
    }

    private float Falloff(float t)
    {
        if (t >= 1f)
        {
            return 0f;
        }
        float hard = Mathf.Clamp(hardness, 0f, 0.99f);
        if (t <= hard)
        {
            return 1f;
        }
        float k = (t - hard) / (1f - hard);
        return 1f - Mathf.SmoothStep(0f, 1f, k);
    }
}
