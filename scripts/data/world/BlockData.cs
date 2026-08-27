using Godot;

// How a block reaches the screen. Invisibility is NOT a value here — a block
// with no surfaces authored emits no geometry, which is what Air, Barrier and
// Opening are.
public enum EBlockRender
{
    // The normal terrain pass (voxel_clip).
    Opaque = 0,
    // The separate water pass — its own shader, its own SurfaceTool surface and
    // hand-tuned render priorities in ChunkMesh. Not derivable from the flags:
    // "visible and transparent" would also describe stained glass.
    Water = 1,
}

// One placeable voxel material: what it looks like on each face, how it behaves
// in the sim, and what it's made of.
//
// A block owns BOTH appearance and physics because appearance is resolved at
// AUTHORING time, never at draw time — a scene stamped into another biome is
// re-textured by rewriting its block ids through a worldgen palette, so a wall
// can never become non-solid by landing somewhere else.
//
// BlockId is the wire id: the per-voxel byte in ChunkState is this value, and
// the shader's per-block uniform tables are indexed by it. Append new ids;
// never renumber, or every saved world re-points.
[GlobalClass]
public partial class BlockData : Resource
{
    [Export] public StringName blockName;

    // Stable wire id — the per-voxel byte. Append the next free value.
    [Export] public int blockId;

    // --- Appearance ---------------------------------------------------------

    // Faces. All three null = invisible (no geometry emitted at all). A block
    // that leaves Side null but authors Top is drawn with Top on every face,
    // which is the common case for single-tile materials.
    [Export] public BlockSurfaceData top;
    [Export] public BlockSurfaceData side;
    [Export] public BlockSurfaceData bottom;

    // Smoothstep band on |surface normal.y| deciding Top/Bottom vs Side.
    //   |y| < x        -> 100% Side
    //   x .. y         -> blend
    //   |y| > y        -> 100% Top (or Bottom, below the horizontal)
    // Per-fragment, off the shaded normal — so one block covers both a grassy
    // plateau and the cliff face falling away from it.
    [Export] public Vector2 wallBand = new Vector2(0.40f, 0.75f);

    [Export] public EBlockRender render = EBlockRender.Opaque;

    // Flat-shaded colour in the WORLD-MAP PAINTER — its per-material authoring
    // view (paving / water option swatches, stamp plan colours), where telling
    // marsh from desert is the point. NOT what the in-game maps draw.
    [Export] public Color minimapColor = new Color(0.3f, 0.3f, 0.3f);

    // What this block reads as on the in-game maps, which show five CATEGORIES
    // rather than per-material colours. Terrain is the default, so a new block
    // is ordinary ground until someone says otherwise.
    [Export] public EMinimapCategory minimapCategory = EMinimapCategory.Terrain;

    // Jitter amplitude where this block's tiles meet a neighbour's. 0 = a
    // straight bisector (man-made walls); higher = jagged (organic ground).
    [Export(PropertyHint.Range, "0,1,0.01")] public float blendNoise = 0f;

    // --- Water ---------------------------------------------------------------
    // Read only where render == Water.

    // What floats on this water — scum, algae, lilypads. Null is bare water.
    [Export] public WaterFilmData waterFilm;

    // How much murkier (+) or clearer (-) this water is than the zone says its
    // water is. Deliberately NOT an absolute: the zone owns the water's hue and
    // its baseline clarity — that is how a region defines its palette — and a
    // water block says how this particular body differs from it. An absolute
    // would mean authoring one scum block per zone, which is exactly what the
    // block palette exists to avoid.
    //
    // Applied as a push toward the endpoint rather than a clamped addition:
    //   d > 0 ? lerp(zoneMuddiness, 1, d) : lerp(zoneMuddiness, 0, -d)
    // so it reads as "this far toward murky from whatever this region is" and
    // still does something in a swamp already sitting at 0.9, where +0.3 added
    // and clamped would do nothing at all.
    //
    // 0 is the identity, which is what makes the standard water block behave
    // exactly as every already-baked world's water does.
    [Export(PropertyHint.Range, "-1,1,0.01")] public float waterTurbidityDelta = 0f;

    // --- Sim behavior -------------------------------------------------------
    // Never palette-resolved: a wall is solid in every biome.

    // Blocks movement, sight and light. False for Air, Water and Opening.
    [Export] public bool solid = true;

    // Light passes through. Water and Opening; drives the light engine's
    // transparent path alongside LightAttenuation.
    [Export] public bool transparent = false;

    // Extra light cost for passing through a transparent block, on top of the
    // normal per-voxel sun decay. Water is 8; Opening stays 0 so a doorway
    // still lets the sun in.
    [Export(PropertyHint.Range, "0,15,1")] public int lightAttenuation = 0;

    // The ceiling cutaway's column rule counts this as part of the wall even
    // though nothing is drawn — so the wall above a door, or between stacked
    // windows, is never cut into a slot. This is the whole reason Opening
    // exists as a distinct block from Air.
    [Export] public bool cutawayIsWall = false;

    // Ground that worldgen laid down as terrain, as opposed to built material
    // (stone blockwork, cobbles) or non-ground (water, air). Gates the passes
    // that only make sense on natural surface: dirt scuff, detail scatter,
    // road grading, prop placement.
    [Export] public bool naturalGround = false;

    // Default per-voxel mesher shape stamped when this block is written and the
    // caller doesn't override. Buildings want All (square edges, flat-shaded);
    // natural ground wants Y (hard height steps, organic walls); ramps None.
    [Export] public SharpAxes defaultShape = SharpAxes.None;

    // The player can climb a wall face made of this. Usually reached through an
    // OVERLAY rather than the voxel's own block — ivy painted over whatever the
    // wall is already made of — which is why ClimbProbe resolves the overlay
    // first, exactly as GroundTypeResolver does for ground type.
    [Export] public bool climbable = false;

    // What GROWS on this rock where it is climbable: the overlay worldgen paints
    // down tall cliff faces (WorldFinish.StampClimbSurfaces) AND the crust the
    // shader marks every mantleable lip with. One field feeds both, which is
    // what keeps a lip matching the wall it is the top of.
    //
    // Keyed per BLOCK rather than per zone because the rock already carries the
    // distinction and the zone cannot: CaveSandstone and CaveLimestone are one
    // zone's caves and everyone else's, so "lichen in desert caves, moss in the
    // rest" is a block split and not a zone one. Per block also means no seam —
    // ChunkState.ZoneIndex is per CHUNK, so a zone-keyed mark would step along
    // 16 m boundaries instead of following the terrain.
    //
    // NOT on BlockSurfaceData: wall surfaces are shared far too widely to carry
    // it (surface_stone is the side of Grass, MarshGround and both cave blocks),
    // and it is a property of the voxel rather than of the texture — the test
    // the root CLAUDE.md sets for that split.
    //
    // Null falls back to BlockCatalog.defaultClimbGrowth; null on both draws no
    // mark at all.
    [Export] public BlockSurfaceData climbGrowthSurface;

    // --- Material -----------------------------------------------------------

    // Movement speed multiplier while standing on this block. Below 1 for mud
    // and deep sand, above 1 for roads.
    [Export(PropertyHint.Range, "0,2,0.01")] public float speedMultiplier = 1.0f;

    // Footstep category. Many blocks share one (desert ground and dune sand
    // both -> Sand).
    [Export] public EGroundType groundType = EGroundType.Grass;

    // Geometric edge roughness in voxels — the DC mesher carves each surface
    // vertex INWARD along its outward normal by a hashed amount in [0, this].
    // Inward-only is what keeps the mesh safe: a vertex can never leave its own
    // cell, so quads can't invert against a neighbour's. Breaks up the metre-
    // scale silhouette only; sub-voxel detail is the normal/height atlas's job.
    [Export(PropertyHint.Range, "0,0.45,0.01")] public float edgeRoughness = 0f;

    // Damping on the vertical component of that carve, so walkable surfaces
    // don't get pitted collision. A floor cell's normal is all-Y and scales to
    // nearly nothing; a wall face has no Y component to damp.
    [Export(PropertyHint.Range, "0,1,0.01")] public float edgeRoughnessVerticalScale = 0.35f;

    // Scooped up when the player digs a bare hole here and finds nothing buried
    // (see Sim.TryDig). Marsh yields mud; most blocks leave this null.
    [Export] public ItemData digItem;

    // Nothing to draw — the mesher emits no geometry for this block, but its
    // flags are still read (Barrier blocks light while drawing nothing).
    public bool IsInvisible()
    {
        return top == null && side == null && bottom == null;
    }

    // Face resolution with the single-tile fallback: a block authoring only Top
    // wears it everywhere.
    public BlockSurfaceData SurfaceFor(EBlockFace face)
    {
        BlockSurfaceData chosen = face switch
        {
            EBlockFace.Top => top,
            EBlockFace.Bottom => bottom ?? side,
            _ => side,
        };
        return chosen ?? top;
    }
}

public enum EBlockFace
{
    Top,
    Side,
    Bottom,
}
