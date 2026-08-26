using System;
using Godot;

// Water stays on the old cubic face-culling mesher. It is mostly planar
// surface — plus the vertical sheet of a waterfall — so there is no reason to
// deform it with DC.
//
// The water is meshed as ONE contiguous volume, dilated a voxel laterally into
// the shore, and skinned with unit quads on the voxel grid. The dilation is what
// covers the land ↔ water seam: land is a DC surface whose vertex can sit
// anywhere inside its own voxel, so where it dips below the waterline a water
// body that stopped at the voxel boundary left the dip dry. The shell cell
// covers it, and where the land instead sits flush or higher, land is opaque and
// in front, so the shell is simply hidden.
//
// Dilating the SET rather than growing each voxel's box is the whole point.
// Per-voxel growth made neighbouring quads share an edge without sharing
// vertices — a T-junction — and coplanar triangles interpolated from different
// endpoints do not rasterize to identical coverage, so the seam cracked into
// single flickering pixels. It also let two cells grow over the same land and
// double-composite. Working on the occupancy set keeps every face a unit quad on
// the grid, so adjacent cells share vertices exactly and both failures are
// structurally impossible.
//
// TOP_EPSILON pulls the exposed top face down a hair so it is never coplanar
// with whatever sits above it. It kills z-fighting at distance where the
// backface's clip-space bias runs out of precision, and it is also what hides
// the shell against land that sits flush with the waterline. Uniform across
// every cell, so it does not reintroduce a mismatch — and it moves the TOP
// FACE ONLY, never the upper edge of a side quad, so the stacked side quads of
// a waterfall still meet exactly instead of cracking open once per voxel.
public static class WaterMesher
{
    private const int N = ChunkState.SIZE;
    private const float TOP_EPSILON = 0.02f;
    private const int MAX_COLUMN_PROBE = 1024;
    private const int TOP_FACE = 0;   // index into Faces / Normals

    private static readonly Vector3[][] Faces =
    {
        new Vector3[] { new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0) },       // Top
        new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1) },       // Bottom
        new Vector3[] { new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0) },       // -Z
        new Vector3[] { new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1) },       // +Z
        new Vector3[] { new(0, 0, 0), new(0, 0, 1), new(0, 1, 1), new(0, 1, 0) },       // -X
        new Vector3[] { new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1) },       // +X
    };

    private static readonly Vector3[] Normals =
    {
        Vector3.Up, Vector3.Down,
        new Vector3(0, 0, -1), new Vector3(0, 0, 1),
        Vector3.Left, Vector3.Right,
    };

    private static readonly Vector3I[] NeighborOffsets =
    {
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, -1), new(0, 0, 1),
        new(-1, 0, 0), new(1, 0, 0),
    };

    public static void Build(
        ChunkState data,
        Func<int, int, int, int> getVoxel,
        MeshBuffer buf,
        int chunkWorldX, int chunkWorldY, int chunkWorldZ,
        out bool hasAnyFace)
    {
        hasAnyFace = false;
        Color color = new Color(1f, 1f, 1f);

        for (int x = 0; x < N; x++)
        {
            for (int z = 0; z < N; z++)
            {
                int wx = chunkWorldX + x;
                int wz = chunkWorldZ + z;

                // Roofed-ness is carried DOWN the column rather than probed per
                // cell: it only changes at a non-Water voxel, so one step per
                // cell replaces a rescan of the water stacked above it.
                bool openAbove = ProbeOpenAbove(getVoxel, wx, chunkWorldY + N - 1, wz);

                for (int y = N - 1; y >= 0; y--)
                {
                    int wy = chunkWorldY + y;
                    int self = getVoxel(wx, wy, wz);

                    // Whether this cell is "roofed" — sealed under solid, with
                    // only water between. Such a cell is in a pocket (water
                    // under rock with no air gap) and none of its faces
                    // represent a real water boundary the player can see;
                    // emitting any of them leaks through the ceiling cutaway
                    // because the clipped solid above no longer writes depth,
                    // leaving the water's deep seabed as the only depth in the
                    // buffer — water front faces happily pass their depth test
                    // against that and paint over the cap.
                    //
                    // Stacked water is TRANSPARENT to this test, which is what
                    // skins a waterfall: every cell of a falling column takes
                    // the state of the free surface above it, so its exposed
                    // sides emit. Don't narrow this to the voxel directly above
                    // — water is water's own roof, so every column deeper than
                    // one voxel then skins nothing but its top quad and a fall
                    // reads as a gap between two pools.
                    //
                    // A SHELL cell takes the exception below: this column is the
                    // one column that cannot answer the question, so it borrows
                    // the answer from the water it shells.
                    bool roofed = !openAbove;
                    // Advance the state for the cell below before any skip.
                    if (!Blocks.IsWater(self))
                    {
                        openAbove = !Blocks.IsSolid(self);
                    }

                    // Where the shore rises above the waterline — a cliff
                    // plunging into the water — the shell cell has rock directly
                    // above it, so reading its own column calls it roofed and
                    // drops it. That takes the top quad with it, and that quad is
                    // load-bearing: DC puts the rock face anywhere in its cell and
                    // edgeRoughness then carves it further back still (0.13 m
                    // measured on stone), so the water stops at the voxel boundary
                    // short of the rock and you see straight down the slot.
                    // Openness belongs to the BODY, not to the column, so ask the
                    // water instead. Only the top face is emitted: the sides would
                    // be inside rock, where the cutaway could strip their occluder
                    // and leak them exactly as the roofed test warns.
                    //
                    // Real water never takes it — a sealed pocket must still drop
                    // out whole, which is what the test is there to do.
                    bool topOnly = false;
                    if (roofed)
                    {
                        if (Blocks.IsWater(self) || !ShelledWaterIsOpen(getVoxel, self, wx, wy, wz))
                        {
                            continue;
                        }
                        topOnly = true;
                    }
                    else if (!InWaterVolume(getVoxel, self, wx, wy, wz))
                    {
                        continue;
                    }

                    Vector3 offset = new(x, y, z);

                    for (int f = 0; f < 6; f++)
                    {
                        if (topOnly && f != TOP_FACE)
                        {
                            continue;
                        }
                        Vector3I no = NeighborOffsets[f];
                        // Cull against the VOLUME, not against Blocks.IsWater,
                        // so the dilated shell is interior and only the outside
                        // of the whole body is skinned.
                        if (InWaterVolume(getVoxel, wx + no.X, wy + no.Y, wz + no.Z))
                        {
                            continue;
                        }

                        Vector3[] verts = Faces[f];
                        Vector3 normal = Normals[f];

                        // CUSTOM0.x is the water block THIS face is made of,
                        // which is what lets one meshed body hold several types:
                        // a clear tarn and the scummy shallows at its edge are
                        // one volume with no seam, and the shader resolves their
                        // optics and film per fragment. Written for a SHELL cell
                        // too, since that face is drawn as the water it shells.
                        //
                        // The other lanes are free. They used to carry the voxel
                        // centre, which nothing ever read.
                        Color custom = new Color(WaterBlockAt(getVoxel, self, wx, wy, wz), 0f, 0f, 0f);

                        // Unit quads on the voxel grid. The TOP_EPSILON drop is
                        // the only deviation, it applies to the whole top face
                        // and to nothing else, and it is uniform — so
                        // neighbouring cells still share vertex positions
                        // exactly, in every direction.
                        Vector3 o = f == TOP_FACE ? offset - new Vector3(0f, TOP_EPSILON, 0f) : offset;
                        Vector3 v0 = verts[0] + o;
                        Vector3 v1 = verts[1] + o;
                        Vector3 v2 = verts[2] + o;
                        Vector3 v3 = verts[3] + o;

                        EmitTri(buf, v0, v2, v1, normal, color, custom);
                        EmitTri(buf, v0, v3, v2, normal, color, custom);
                        hasAnyFace = true;
                    }
                }
            }
        }
    }

    // Which water block this face is drawn as. A water cell answers with itself;
    // a SHELL cell is solid — it is the one-voxel dilation into the shore — so it
    // borrows from the water it shells, exactly as CoverLayerAt and
    // ShelledWaterIsOpen do. Falling back to the default would put a ring of
    // ordinary water around every scummy pond.
    private static float WaterBlockAt(Func<int, int, int, int> getVoxel, int self, int wx, int wy, int wz)
    {
        if (Blocks.IsWater(self))
        {
            return self;
        }
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0)
                {
                    continue;
                }
                int v = getVoxel(wx + dx, wy, wz + dz);
                if (Blocks.IsWater(v))
                {
                    return v;
                }
            }
        }
        return Blocks.DefaultWaterId;
    }

    // True iff a SOLID voxel is covered by the water mesh's top quad: it is in
    // the dilated volume (shell cell) and has air directly above, so Build
    // skins its top face at the waterline. Nothing above it is a Water voxel,
    // yet its whole DC surface sits under water — placement passes gate upright
    // scatter on this so grass doesn't root in the visible shallows.
    public static bool IsCoveredShell(Func<int, int, int, int> getVoxel, int wx, int wy, int wz)
    {
        return InWaterVolume(getVoxel, wx, wy, wz)
            && getVoxel(wx, wy + 1, wz) == Blocks.AirId;
    }

    // The water VOLUME: real water, plus a one-voxel lateral shell into the
    // solid it touches. Lateral only — water must not climb over land or seep
    // down into bedrock. A pure function of world voxels, so two chunks sharing
    // a boundary cell always agree and no seam can appear between them.
    //
    // 8-CONNECTED, one voxel, and that is exactly the submerged set — not a
    // margin picked for safety. At the waterline row every non-solid voxel is
    // water (worldgen fills to a level), so a solid voxel's DC cap sags under
    // the waterline iff one of the four cells forming it has a non-solid corner
    // — iff one of the nine columns in its 3x3 is water. A column two out lands
    // flush on the plane, so a wider shell would only bury quads in dry land.
    //
    // Diagonal columns are not a rare corner case: a shoreline at any angle to
    // the grid is a staircase and EVERY step has one, so 4-connectivity left a
    // submerged notch at each step — 0.20 m deep on a 45-degree graded shore,
    // which is what made a slope into water read as stepped. `water_shore_check`
    // measures it.
    private static bool InWaterVolume(Func<int, int, int, int> getVoxel, int wx, int wy, int wz)
    {
        return InWaterVolume(getVoxel, getVoxel(wx, wy, wz), wx, wy, wz);
    }

    // Overload for callers that already read the cell's own type.
    private static bool InWaterVolume(Func<int, int, int, int> getVoxel, int v, int wx, int wy, int wz)
    {
        if (Blocks.IsWater(v))
        {
            return true;
        }
        if (!Blocks.IsSolid(v))
        {
            return false;
        }
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0)
                {
                    continue;
                }
                if (Blocks.IsWater(getVoxel(wx + dx, wy, wz + dz)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // Does this solid cell shell water that is open to the air above? Answers
    // the roofed question for a shell cell standing under rock, and doubles as
    // its in-volume test — finding an open water neighbour proves both.
    //
    // Only ever reached for a cell the roofed test already rejected, so the cost
    // lands on solid-under-solid — the whole underground, where it can never
    // find anything. It has to be rock-driven anyway: the cheap inversion (let a
    // free-surface water cell mark its solid neighbours) would have two chunks
    // disagree about a column on their shared boundary, and being a pure
    // function of world voxels is what makes this mesher seamless. Measured
    // worst case, an all-rock chunk: 0.14 -> 0.59 ms, against 5.8 ms for the
    // terrain mesh of the same chunk (`water_shore_check` prints both).
    private static bool ShelledWaterIsOpen(Func<int, int, int, int> getVoxel, int self, int wx, int wy, int wz)
    {
        if (!Blocks.IsSolid(self))
        {
            return false;
        }
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0)
                {
                    continue;
                }
                int nx = wx + dx;
                int nz = wz + dz;
                if (Blocks.IsWater(getVoxel(nx, wy, nz)) && ProbeOpenAbove(getVoxel, nx, wy, nz))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // Is the first non-Water voxel above (wx, wy, wz) something other than
    // solid — i.e. is the water column this cell belongs to open rather than
    // capped by rock? Seeds the per-column walk in Build at the chunk's top
    // cell; every cell below is one step off its neighbour above.
    //
    // Missing chunks read as Air, so this terminates at the top of the world;
    // MAX_COLUMN_PROBE is a runaway guard against a getVoxel that doesn't.
    private static bool ProbeOpenAbove(Func<int, int, int, int> getVoxel, int wx, int wy, int wz)
    {
        for (int i = 1; i <= MAX_COLUMN_PROBE; i++)
        {
            int v = getVoxel(wx, wy + i, wz);
            if (!Blocks.IsWater(v))
            {
                return !Blocks.IsSolid(v);
            }
        }
        return true;
    }

    private static void EmitTri(MeshBuffer buf, Vector3 a, Vector3 b, Vector3 c, Vector3 n, Color col, Color custom)
    {
        buf.Add(a, n, col, custom);
        buf.Add(b, n, col, custom);
        buf.Add(c, n, col, custom);
    }
}
