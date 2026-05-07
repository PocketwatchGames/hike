using Godot;

// Color palette for the minimap, authored as semantic named fields rather
// than a flat 64-entry array. Each field corresponds to a tile (or band of
// a tile) the voxel renderer can resolve to.
//
// At first read, BuildTable() expands the named fields into a 64-entry
// internal array indexed by VoxelTypeInfo.TILE_* layer ids — bands cycle
// the variants-per-band run, so e.g. all four grass-lowland variants get
// painted with `GrassLowland`. Tile ids that don't have a named field fall
// back to `Unauthored`.
//
// This means authors see a clean Inspector list ("GrassLowland", "Water",
// "DesertSand") instead of memorizing index 27 vs 28.
[GlobalClass]
public partial class MinimapTileColors : Resource
{
    public const int Size = VoxelTypeInfo.TILE_VARIANT_TABLE_SIZE;

    // Reserved palette slot for wall interiors (slice view: any column that's
    // solid all the way through the slice with no air above). Doesn't
    // correspond to a renderer tile id — the slice generator writes this
    // index directly so all wall pixels read as the authored Wall color
    // regardless of voxel type or kit.
    public const int WALL_SLOT = 32;

    [ExportGroup("Stone & Built")]
    [Export] public Color Stone = new Color(0.5f, 0.5f, 0.52f);
    [Export] public Color Cobblestone = new Color(0.42f, 0.4f, 0.35f);
    // Indoor wall interior (slice view: solid columns viewed from above).
    // Used for ALL biomes' wall pixels — kit lookup is bypassed for walls
    // so a tunnel through marsh terrain reads the same dark grey as one
    // through stone.
    [Export] public Color Wall = new Color(0.18f, 0.18f, 0.20f);

    [ExportGroup("Grass — Elevation Bands")]
    // Band 0: sea-level / lowland grass (forest, plains).
    [Export] public Color GrassLowland = new Color(0.30f, 0.55f, 0.20f);
    // Band 1: mid-elevation grass (foothills).
    [Export] public Color GrassMid = new Color(0.34f, 0.50f, 0.22f);
    // Band 2: high-elevation grass (mountainside, washed yellow-green).
    [Export] public Color GrassHighland = new Color(0.50f, 0.50f, 0.20f);
    // Band 3: alpine / peak grass (stone-brown).
    [Export] public Color GrassAlpine = new Color(0.42f, 0.34f, 0.20f);

    [ExportGroup("Water")]
    [Export] public Color Water = new Color(0.20f, 0.40f, 0.65f);

    [ExportGroup("Desert")]
    // Band 0: sea-level desert shelf (TILE_DESERT_TOP at low elevation).
    [Export] public Color DesertShelf = new Color(0.85f, 0.72f, 0.40f);
    // Band 1: dune sand above sea level (TILE_DESERT_SAND).
    [Export] public Color DesertSand = new Color(0.78f, 0.58f, 0.30f);
    // Cliff faces in the desert kit.
    [Export] public Color DesertWall = new Color(0.65f, 0.45f, 0.22f);
    // Sandstone cave floor.
    [Export] public Color DesertCave = new Color(0.55f, 0.42f, 0.26f);

    [ExportGroup("Wetlands & Overlays")]
    [Export] public Color Marsh = new Color(0.60f, 0.55f, 0.20f);
    [Export] public Color DirtOverlay = new Color(0.45f, 0.32f, 0.20f);
    [Export] public Color FieldOverlay = new Color(0.65f, 0.62f, 0.30f);

    [ExportGroup("Fallback")]
    // Used for any tile id that doesn't have a named field above. Visible
    // as a sanity-check color so an unhandled tile stands out rather than
    // silently rendering as one of the existing colors.
    [Export] public Color Unauthored = new Color(0.30f, 0.30f, 0.30f);

    // Lazy lookup table — built on first Get() call and cached. Field edits
    // in the Inspector only take effect after game restart (Godot reloads
    // the .tres at startup), which is fine for an authoring resource.
    private Color[] _table;

    public Color Get(int tileId)
    {
        if (_table == null)
        {
            BuildTable();
        }
        if (tileId < 0 || tileId >= _table.Length)
        {
            return Unauthored;
        }
        return _table[tileId];
    }

    private void BuildTable()
    {
        _table = new Color[Size];
        for (int i = 0; i < Size; i++)
        {
            _table[i] = Unauthored;
        }

        _table[VoxelTypeInfo.TILE_STONE] = Stone;
        _table[WALL_SLOT] = Wall;

        // Grass: 4 bands × 4 variants (= 16 layers starting at TILE_GRASS_TOP).
        // Variants within a band aren't differentiated on the minimap — fill
        // each band's variant run with that band's color.
        WriteBand(VoxelTypeInfo.TILE_GRASS_TOP, 0, 4, GrassLowland);
        WriteBand(VoxelTypeInfo.TILE_GRASS_TOP, 1, 4, GrassMid);
        WriteBand(VoxelTypeInfo.TILE_GRASS_TOP, 2, 4, GrassHighland);
        WriteBand(VoxelTypeInfo.TILE_GRASS_TOP, 3, 4, GrassAlpine);

        _table[VoxelTypeInfo.TILE_WATER] = Water;

        // Cobblestone: 1 band × 4 variants.
        WriteBand(VoxelTypeInfo.TILE_COBBLESTONE, 0, 4, Cobblestone);

        // Dirt + Field overlays.
        WriteBand(VoxelTypeInfo.TILE_DIRT_OVERLAY, 0, 4, DirtOverlay);
        _table[VoxelTypeInfo.TILE_FIELD_OVERLAY] = FieldOverlay;

        // Desert top: 2 bands × 1 variant. Band 0 = sea-level shelf, band 1
        // = dune sand. (TILE_DESERT_SAND is the named alias for band 1's
        // single variant; both indices resolve to the same layer.)
        _table[VoxelTypeInfo.TILE_DESERT_TOP] = DesertShelf;
        _table[VoxelTypeInfo.TILE_DESERT_SAND] = DesertSand;
        _table[VoxelTypeInfo.TILE_DESERT_WALL] = DesertWall;
        _table[VoxelTypeInfo.TILE_DESERT_CAVE] = DesertCave;

        _table[VoxelTypeInfo.TILE_MARSH] = Marsh;
    }

    private void WriteBand(int baseTile, int bandIndex, int variantsPerBand, Color color)
    {
        int start = baseTile + bandIndex * variantsPerBand;
        int end = start + variantsPerBand;
        if (end > _table.Length)
        {
            end = _table.Length;
        }
        for (int i = start; i < end; i++)
        {
            _table[i] = color;
        }
    }
}
