using Godot;

// Flattened per-block lookup for the sim's hot paths.
//
// Every field on BlockData that the mesher, light engine, nav grid or physics
// reads per voxel is copied into a plain managed array here, indexed by block
// id. Those loops run millions of times per world build, and a Resource
// property read crosses the managed/native boundary — see the boundary note in
// CLAUDE.md. Bound once per world load; block resources are immutable after
// load so there is nothing to invalidate.
//
// The four named ids are the blocks the engine itself special-cases. They are
// resolved BY NAME at bind time rather than hardcoded, so the catalog still
// owns the numbering.
public static class Blocks
{
    public static int AirId { get; private set; }
    // DELIBERATELY NOT "WaterId". There are several water blocks now (clear,
    // murky, the scummy ones, ice later), so an equality test against one id is
    // wrong wherever it means "is this water" — ask Blocks.IsWater(id).
    //
    // This is the block to WRITE when something fills a column with ordinary
    // water and has no reason to pick a type: worldgen's fill, the painter's
    // default, an editor brush. Its turbidity delta is 0, so it is the identity
    // and every world baked before water had types reads back unchanged.
    public static int DefaultWaterId { get; private set; }
    public static int BarrierId { get; private set; }
    public static int OpeningId { get; private set; }
    public static int StoneId { get; private set; }
    // Stand-in natural ground for code with no kit in hand — probe scaffolding
    // and the fallback when a kit names no block.
    public static int GroundId { get; private set; }

    private static bool[] _solid;
    private static bool[] _empty;
    private static bool[] _water;
    private static bool[] _transparent;
    private static bool[] _cutawayIsWall;
    private static bool[] _invisible;
    private static bool[] _naturalGround;
    private static bool[] _climbable;
    private static int[] _climbGrowthLayer;
    private static int[] _attenuation;
    private static SharpAxes[] _defaultShape;
    private static float[] _blendNoise;
    private static float[] _edgeRoughness;
    private static float[] _edgeRoughnessVerticalScale;
    private static float[] _waterTurbidity;

    // Fills LOCAL tables and publishes them at the end, so a reader on another
    // thread only ever sees a finished set.
    //
    // The catalog is genuinely global — block ids are the same in every world,
    // which is what lets a scene stamped from one world be recognised in
    // another — but the TABLES are rebuilt on every world activation, and the
    // world-map painter's bake does that from a background thread while a live
    // main thread is reading `Blocks.IsSolid`. Assigning the fields first and
    // filling them afterwards, which is what this did, left a window where a
    // reader saw an all-false array and every voxel in the world was empty.
    public static void Bind()
    {
        int n = BlockCatalog.MAX_BLOCKS;
        var solid = new bool[n];
        var empty = new bool[n];
        var water = new bool[n];
        var transparent = new bool[n];
        var cutawayIsWall = new bool[n];
        var invisible = new bool[n];
        var naturalGround = new bool[n];
        var climbable = new bool[n];
        var climbGrowthLayer = new int[n];
        var attenuation = new int[n];
        var defaultShape = new SharpAxes[n];
        var blendNoise = new float[n];
        var edgeRoughness = new float[n];
        var edgeRoughnessVerticalScale = new float[n];
        var waterTurbidity = new float[n];

        BlockCatalog catalog = BlockCatalog.Active;
        for (int id = 0; id < n; id++)
        {
            BlockData b = catalog.GetById(id);
            if (b == null)
            {
                // An unauthored slot behaves as empty space, so a world holding
                // a stale id renders nothing rather than a solid black cube.
                empty[id] = true;
                invisible[id] = true;
                continue;
            }
            solid[id] = b.solid;
            water[id] = b.render == EBlockRender.Water;
            waterTurbidity[id] = b.waterTurbidityDelta;
            transparent[id] = b.transparent;
            cutawayIsWall[id] = b.cutawayIsWall;
            invisible[id] = b.IsInvisible();
            naturalGround[id] = b.naturalGround;
            climbable[id] = b.climbable;
            BlockSurfaceData growth = catalog.ClimbGrowthFor(id);
            climbGrowthLayer[id] = growth != null ? growth.atlasBaseIndex : -1;
            attenuation[id] = b.lightAttenuation;
            defaultShape[id] = b.defaultShape;
            blendNoise[id] = b.blendNoise;
            edgeRoughness[id] = b.edgeRoughness;
            edgeRoughnessVerticalScale[id] = b.edgeRoughnessVerticalScale;
            // "Nothing here" — space you can stand in, see through and walk out
            // of. Water is NOT empty: it is non-solid but it is content.
            empty[id] = !b.solid && b.IsInvisible();
        }

        // Published only now that every table is complete.
        _solid = solid;
        _empty = empty;
        _water = water;
        _transparent = transparent;
        _cutawayIsWall = cutawayIsWall;
        _invisible = invisible;
        _naturalGround = naturalGround;
        _climbable = climbable;
        _climbGrowthLayer = climbGrowthLayer;
        _attenuation = attenuation;
        _defaultShape = defaultShape;
        _blendNoise = blendNoise;
        _edgeRoughness = edgeRoughness;
        _edgeRoughnessVerticalScale = edgeRoughnessVerticalScale;
        _waterTurbidity = waterTurbidity;

        AirId = catalog.GetIdByName("Air");
        DefaultWaterId = catalog.GetIdByName("Water");
        BarrierId = catalog.GetIdByName("Barrier");
        OpeningId = catalog.GetIdByName("Opening");
        StoneId = catalog.GetIdByName("Stone");
        GroundId = catalog.GetIdByName("Grass");
    }

    // Blocks movement, sight and light.
    public static bool IsSolid(int id) => _solid[id];

    // "Nothing here" — air or an opening. Use this rather than `== AirId`
    // wherever the question is about EMPTINESS: an Opening is a doorway void and
    // is empty in every sense except the cutaway's, so an AirId compare silently
    // treats one as occupied. `== AirId` stays correct where the question is
    // about authored CONTENT (a subscene's bounds include the openings it was
    // drawn with).
    public static bool IsEmpty(int id) => _empty[id];

    public static bool IsWater(int id) => _water[id];

    // How much murkier (+) or clearer (-) than its zone this water block is.
    // Built once at Bind and only read afterwards, which is what keeps it safe
    // to call from a mesher running on a worker thread.
    public static float WaterTurbidity(int id) => _waterTurbidity[id];

    // Light passes through — water and openings.
    public static bool IsTransparent(int id) => _transparent[id];

    // Nothing is drawn for this block, though its flags still apply.
    public static bool IsInvisible(int id) => _invisible[id];

    // Terrain worldgen laid down, as opposed to built material or non-ground.
    // Gates dirt scuff, detail scatter, road grading and prop placement.
    public static bool IsNaturalGround(int id) => _naturalGround[id];

    // A wall face of this block can be climbed. Note the usual carrier is an
    // overlay painted over some other block, so gameplay asks ClimbProbe rather
    // than calling this with the raw voxel id.
    public static bool IsClimbable(int id) => _climbable[id];

    // Atlas layer of the crust this block grows where it is climbable, or -1.
    // Read per voxel by the mesher's lip scan, hence the flattened table.
    public static int ClimbGrowthLayer(int id) => _climbGrowthLayer[id];

    // Debug scaffolding for the `climb_mark` console command: makes an ordinary
    // wall climbable for the running session so surface climbing can be played
    // before an ivy overlay is authored. Not persisted anywhere — the authored
    // path is BlockData.climbable, reached through an overlay.
    public static void SetClimbableForDebug(int id, bool value)
    {
        if (_climbable != null && id >= 0 && id < _climbable.Length)
        {
            _climbable[id] = value;
        }
    }

    // The ceiling cutaway's column rule reads this as part of the wall.
    public static bool CutawayIsWall(int id) => _cutawayIsWall[id];

    // Extra light cost through a transparent block, on top of the normal decay.
    public static int LightAttenuation(int id) => _attenuation[id];

    public static SharpAxes DefaultShape(int id) => _defaultShape[id];

    // Jitter amplitude where this block's tiles meet a neighbour's.
    public static float BlendNoise(int id) => _blendNoise[id];

    public static float EdgeRoughness(int id) => _edgeRoughness[id];

    public static float EdgeRoughnessVerticalScale(int id) => _edgeRoughnessVerticalScale[id];
}
