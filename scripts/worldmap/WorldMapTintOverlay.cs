using Godot;

// CVar-gated in-world overlay that tints terrain by per-chunk region / zone
// colour. Builds a per-chunk-column colour LUT from the live world and feeds the
// terrain shader globals (see shaders/worldmap_tint.gdshaderinc). When the mode
// is off the strength global is 0 and the shader hook is a strict no-op, so the
// shipped game pays nothing — this is authoring/debug only and ships disabled.
//
// Driven by the `worldmap_tint` (mode) and `worldmap_tint_strength` CVars. The
// painter also calls Refresh() after region/zone edits so the in-world tint
// tracks painting while the overlay is on. Only walks chunk *columns* (XZ) of
// the resident world — acceptable for a debug overlay that is off by default.
public static class WorldMapTintOverlay
{
    public const int MODE_OFF = 0;
    public const int MODE_REGION = 1;
    public const int MODE_ZONE = 2;

    private static readonly StringName GTex = "worldmap_tint_tex";
    private static readonly StringName GOrigin = "worldmap_tint_origin";
    private static readonly StringName GSize = "worldmap_tint_size";
    private static readonly StringName GStrength = "worldmap_tint_strength";

    private static int _mode = MODE_OFF;
    private static float _strength = 0.7f;
    // Held so the texture backing the global RID isn't collected while live.
    private static ImageTexture _lut;

    public static void SetMode(int mode)
    {
        _mode = mode;
        Refresh();
    }

    public static void SetStrength(float strength)
    {
        _strength = strength;
        Refresh();
    }

    public static void Refresh()
    {
        WorldState ws = World.Current?.WorldState;
        if (_mode == MODE_OFF || ws == null)
        {
            RenderingServer.GlobalShaderParameterSet(GStrength, 0f);
            return;
        }

        int sizeX = ws.Max.X - ws.Min.X + 1;
        int sizeZ = ws.Max.Z - ws.Min.Z + 1;
        if (sizeX <= 0 || sizeZ <= 0)
        {
            RenderingServer.GlobalShaderParameterSet(GStrength, 0f);
            return;
        }

        var img = Image.CreateEmpty(sizeX, sizeZ, false, Image.Format.Rgb8);
        for (int lcx = 0; lcx < sizeX; lcx++)
        {
            for (int lcz = 0; lcz < sizeZ; lcz++)
            {
                img.SetPixel(lcx, lcz, ColumnColor(ws, ws.Min.X + lcx, ws.Min.Z + lcz));
            }
        }
        _lut = ImageTexture.CreateFromImage(img);

        RenderingServer.GlobalShaderParameterSet(GTex, _lut);
        RenderingServer.GlobalShaderParameterSet(GOrigin,
            new Vector2(ws.Min.X * ChunkState.SIZE, ws.Min.Z * ChunkState.SIZE));
        RenderingServer.GlobalShaderParameterSet(GSize, new Vector2(sizeX, sizeZ));
        RenderingServer.GlobalShaderParameterSet(GStrength, _strength);
    }

    private static Color ColumnColor(WorldState ws, int cx, int cz)
    {
        // Region/zone are per-chunk; take the index from the topmost loaded
        // chunk in the column.
        for (int cy = ws.Max.Y; cy >= ws.Min.Y; cy--)
        {
            ChunkState chunk = ws.GetChunk(new Vector3I(cx, cy, cz));
            if (chunk == null)
            {
                continue;
            }
            return _mode == MODE_REGION
                ? WorldMapState.RegionColor(chunk.RegionIndex)
                : WorldMapState.ZoneColor(chunk.ZoneIndex);
        }
        return Colors.Black;
    }
}
