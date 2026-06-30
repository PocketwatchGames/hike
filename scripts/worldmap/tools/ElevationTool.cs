using Godot;

public enum EBrushOp
{
    Raise = 0,
    Lower = 1,
    Flatten = 2,
    Smooth = 3,
}

// Sculpts the per-column elevation layer. AdjustLevel sets the global ocean
// elevation. The view tints anything underwater.
public class ElevationTool : IWorldMapTool
{
    public string Name => "Elevation";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 12f;

    public EBrushOp Op = EBrushOp.Raise;
    public float StrengthPerStep = 0.04f;

    public ElevationTool()
    {
        View = new ElevationView();
    }

    public string StatusText(WorldMapState ctx) => Op.ToString();
    public string LevelText(WorldMapState ctx) => $"Ocean Y={ctx.SeaLevel}";

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        EBrushOp op = erase ? EBrushOp.Lower : Op;
        int w = ctx.Data.ImageWidth;
        int h = ctx.Data.ImageHeight;
        float target = ctx.Elevation01(texel.X, texel.Y);   // Flatten reference

        brush.Stamp(texel, Radius, w, h, (px, pz, weight) =>
        {
            float v = ctx.Elevation.GetPixel(px, pz).R;
            float k = brush.flow * weight;
            switch (op)
            {
                case EBrushOp.Raise:
                    v += StrengthPerStep * k * (1f + brush.NoiseAt(px, pz));
                    break;
                case EBrushOp.Lower:
                    v -= StrengthPerStep * k * (1f + brush.NoiseAt(px, pz));
                    break;
                case EBrushOp.Flatten:
                    v = Mathf.Lerp(v, target, k);
                    break;
                case EBrushOp.Smooth:
                    v = Mathf.Lerp(v, BoxAverage(ctx.Elevation, px, pz, w, h), k);
                    break;
            }
            ctx.Elevation.SetPixel(px, pz, new Color(Mathf.Clamp(v, 0f, 1f), 0f, 0f, 1f));
        });
    }

    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = System.Enum.GetValues<EBrushOp>().Length;
        Op = (EBrushOp)(((int)Op + dir + n) % n);
    }

    // Ocean elevation. Changing it reshapes every column's water; the 2D views
    // re-read SeaLevel on the painter's next full rebuild.
    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        ctx.SeaLevel += dir;
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

// Hypsometric land ramp; underwater columns tinted blue by depth.
public class ElevationView : IWorldMapView
{
    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        Color c = WorldMapState.Hypsometric(ctx.Elevation01(px, pz));
        if (ctx.Underwater(px, pz))
        {
            float depth = ctx.WaterSurface(px, pz) - ctx.TerrainHeight(px, pz);
            float t = Mathf.Clamp(depth / 16f, 0.25f, 0.8f);
            c = c.Lerp(new Color(0.1f, 0.3f, 0.55f), t);
        }
        return c;
    }
}
