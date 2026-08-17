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
    public static int WaterId { get; private set; }
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
    private static int[] _attenuation;
    private static SharpAxes[] _defaultShape;
    private static float[] _blendNoise;
    private static float[] _edgeRoughness;
    private static float[] _edgeRoughnessVerticalScale;

    public static void Bind()
    {
        int n = BlockCatalog.MAX_BLOCKS;
        _solid = new bool[n];
        _empty = new bool[n];
        _water = new bool[n];
        _transparent = new bool[n];
        _cutawayIsWall = new bool[n];
        _invisible = new bool[n];
        _naturalGround = new bool[n];
        _climbable = new bool[n];
        _attenuation = new int[n];
        _defaultShape = new SharpAxes[n];
        _blendNoise = new float[n];
        _edgeRoughness = new float[n];
        _edgeRoughnessVerticalScale = new float[n];

        BlockCatalog catalog = BlockCatalog.Active;
        for (int id = 0; id < n; id++)
        {
            BlockData b = catalog.GetById(id);
            if (b == null)
            {
                // An unauthored slot behaves as empty space, so a world holding
                // a stale id renders nothing rather than a solid black cube.
                _empty[id] = true;
                _invisible[id] = true;
                continue;
            }
            _solid[id] = b.solid;
            _water[id] = b.render == EBlockRender.Water;
            _transparent[id] = b.transparent;
            _cutawayIsWall[id] = b.cutawayIsWall;
            _invisible[id] = b.IsInvisible();
            _naturalGround[id] = b.naturalGround;
            _climbable[id] = b.climbable;
            _attenuation[id] = b.lightAttenuation;
            _defaultShape[id] = b.defaultShape;
            _blendNoise[id] = b.blendNoise;
            _edgeRoughness[id] = b.edgeRoughness;
            _edgeRoughnessVerticalScale[id] = b.edgeRoughnessVerticalScale;
            // "Nothing here" — space you can stand in, see through and walk out
            // of. Water is NOT empty: it is non-solid but it is content.
            _empty[id] = !b.solid && b.IsInvisible();
        }

        AirId = catalog.GetIdByName("Air");
        WaterId = catalog.GetIdByName("Water");
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
