using Godot;

// What a brush stroke does to the elevation layer. Op is resolved per-stroke by
// the painter (e.g. right-click forces Lower); the brush just executes it.
public enum EBrushOp
{
    Raise = 0,
    Lower = 1,
    Flatten = 2,
    Smooth = 3,
}

// Authoring tuning for a paint brush. One brush drives every layer op via a
// shared falloff/flow stamp — v1 only paints the elevation layer, but the
// engine (Stamp) is layer-agnostic so future zone/region/prop brushes reuse it.
// Tunings are [Export] so brush presets can be authored as .tres files.
[GlobalClass]
public partial class WorldMapBrush : Resource
{
    [Export] public EBrushOp Op = EBrushOp.Raise;

    // Stamp radius in column texels (one texel == one voxel column).
    [Export(PropertyHint.Range, "0.5,256,0.5")] public float Radius = 12f;

    // Inner fraction of the radius that stays at full strength before the soft
    // edge falls off. 0 = soft from the centre, ~1 = a hard-edged disk.
    [Export(PropertyHint.Range, "0,1,0.01")] public float Hardness = 0.4f;

    // Per-application strength multiplier (0..1). Held drags apply once per
    // mouse-motion event, so Flow controls how fast a stroke builds up.
    [Export(PropertyHint.Range, "0,1,0.01")] public float Flow = 0.5f;

    // Normalized elevation change per application at full flow/weight, for
    // Raise/Lower. Sub-0.01 magnitudes are meaningful here, so the range step
    // is fine (see the [Export]-precision note in CLAUDE.md).
    [Export(PropertyHint.Range, "0.001,0.5,0.001")] public float StrengthPerStep = 0.04f;

    // Noise modulation for Raise/Lower (0 = smooth). Painted into the layer at
    // stamp time so the 2D image shows exactly what bakes.
    [Export(PropertyHint.Range, "0,1,0.01")] public float NoiseAmount = 0f;
    [Export(PropertyHint.Range, "0.001,1,0.0001")] public float NoiseFrequency = 0.05f;

    // Lazily-built, frequency-synced noise field. Not a Godot sub-resource —
    // just a runtime helper, so it never serializes into the .tres.
    private FastNoiseLite _noise;

    private FastNoiseLite Noise()
    {
        if (_noise == null)
        {
            _noise = new FastNoiseLite();
            _noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        }
        _noise.Frequency = NoiseFrequency;
        return _noise;
    }

    // Apply one stamp of `op` centred on `center` (in texel coords) into `img`
    // (Image.Format.Rf, R = normalized elevation 0..1). Returns the clamped
    // bounding rect of touched texels so the caller can re-bake / re-colour
    // exactly that region.
    public Rect2I Stamp(Image img, Vector2I center, EBrushOp op)
    {
        int w = img.GetWidth();
        int h = img.GetHeight();
        int r = Mathf.CeilToInt(Radius);
        int x0 = Mathf.Max(0, center.X - r);
        int x1 = Mathf.Min(w - 1, center.X + r);
        int z0 = Mathf.Max(0, center.Y - r);
        int z1 = Mathf.Min(h - 1, center.Y + r);
        if (x1 < x0 || z1 < z0)
        {
            return new Rect2I(0, 0, 0, 0);
        }

        // Flatten levels the whole stamp toward the value under the centre at
        // the moment of application.
        int cx = Mathf.Clamp(center.X, 0, w - 1);
        int cz = Mathf.Clamp(center.Y, 0, h - 1);
        float target = img.GetPixel(cx, cz).R;

        FastNoiseLite noise = NoiseAmount > 0f ? Noise() : null;

        for (int px = x0; px <= x1; px++)
        {
            for (int pz = z0; pz <= z1; pz++)
            {
                float dx = px - center.X;
                float dz = pz - center.Y;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > Radius)
                {
                    continue;
                }
                float weight = Falloff(dist / Radius);
                if (weight <= 0f)
                {
                    continue;
                }

                float v = img.GetPixel(px, pz).R;
                float k = Flow * weight;
                switch (op)
                {
                    case EBrushOp.Raise:
                    {
                        float n = noise != null ? noise.GetNoise2D(px, pz) * NoiseAmount : 0f;
                        v += StrengthPerStep * k * (1f + n);
                        break;
                    }
                    case EBrushOp.Lower:
                    {
                        float n = noise != null ? noise.GetNoise2D(px, pz) * NoiseAmount : 0f;
                        v -= StrengthPerStep * k * (1f + n);
                        break;
                    }
                    case EBrushOp.Flatten:
                    {
                        v = Mathf.Lerp(v, target, k);
                        break;
                    }
                    case EBrushOp.Smooth:
                    {
                        v = Mathf.Lerp(v, BoxAverage(img, px, pz, w, h), k);
                        break;
                    }
                }
                img.SetPixel(px, pz, new Color(Mathf.Clamp(v, 0f, 1f), 0f, 0f, 1f));
            }
        }

        return new Rect2I(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
    }

    // Disk falloff: full strength inside the Hardness fraction, smoothstep edge
    // out to the radius, zero beyond. `t` is dist / Radius in [0, 1].
    private float Falloff(float t)
    {
        if (t >= 1f)
        {
            return 0f;
        }
        float hard = Mathf.Clamp(Hardness, 0f, 0.99f);
        if (t <= hard)
        {
            return 1f;
        }
        float k = (t - hard) / (1f - hard);
        return 1f - Mathf.SmoothStep(0f, 1f, k);
    }

    private static float BoxAverage(Image img, int px, int pz, int w, int h)
    {
        float sum = 0f;
        int count = 0;
        for (int dx = -1; dx <= 1; dx++)
        {
            int nx = px + dx;
            if (nx < 0 || nx >= w)
            {
                continue;
            }
            for (int dz = -1; dz <= 1; dz++)
            {
                int nz = pz + dz;
                if (nz < 0 || nz >= h)
                {
                    continue;
                }
                sum += img.GetPixel(nx, nz).R;
                count++;
            }
        }
        return count > 0 ? sum / count : img.GetPixel(px, pz).R;
    }
}
