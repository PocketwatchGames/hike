using System.Collections.Generic;
using Godot;

// Paints standing water bodies (lakes, rivers) by writing a per-column water
// surface height, independent of the global ocean elevation. AdjustLevel sets
// the surface Y the brush paints; erase removes painted water (back to ocean).
public class WaterTool : IWorldMapTool
{
    public string Name => "Water";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 10f;

    // Absolute world-voxel Y the painted water surface rises to.
    public int ActiveLevel = 4;

    public WaterTool()
    {
        View = new WaterView();
    }

    public string StatusText(WorldMapState ctx) => "Paint Water";
    public string LevelText(WorldMapState ctx) => $"Water Y={ActiveLevel}";

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        // Encode the absolute surface Y back into the column-height normalized
        // value (ColumnHeight(v) == SeaLevel + v*Max). 0 = no painted water.
        float v01 = erase
            ? 0f
            : Mathf.Clamp((ActiveLevel - ctx.SeaLevel) / ctx.Data.MaxElevationVoxels, 0f, 1f);

        Rect2I rect = brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            ctx.Water.SetPixel(px, pz, new Color(v01, 0f, 0f, 1f));
        });
        if (rect.Size.X <= 0)
        {
            return;
        }

        var changed = new List<Vector3I>();
        ctx.StampColumns(rect, changed);
        ctx.Commit(changed);
    }

    public void Cycle(WorldMapState ctx, int dir)
    {
        // No secondary parameter — AdjustLevel drives the surface height.
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        ActiveLevel += dir;
    }
}

// Painted/ocean water shown as blue by depth; dry land as a faint elevation grey.
public class WaterView : IWorldMapView
{
    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        int th = ctx.TerrainHeight(px, pz);
        int surf = ctx.WaterSurface(px, pz);
        if (surf > th)
        {
            float t = Mathf.Clamp((surf - th) / 24f, 0.15f, 0.9f);
            return new Color(0.25f, 0.5f, 0.85f).Lerp(new Color(0.02f, 0.08f, 0.3f), t);
        }
        float v = ctx.Elevation01(px, pz);
        float g = 0.3f + 0.5f * v;
        return new Color(g * 0.85f, g, g * 0.75f);
    }
}
