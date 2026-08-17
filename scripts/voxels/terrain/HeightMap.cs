using System;
using System.Collections.Generic;
using Godot;

// The world's per-column height field — the single structure every terrain
// approach produces and every downstream worldgen pass consumes. Keeping it
// here rather than inside one generator is what lets the approaches stay
// isolated: they share this contract and nothing else.
//
// All arrays are indexed as [wx - WorldMinX, wz - WorldMinZ].
//
//   Plateau: the flat-ground reference. On the plateau approach it is the
//            quantized terrain level, and Height > Plateau marks a ramp; on
//            approaches with no such notion it mirrors Height, which makes
//            IsFlatDryGrassAt a pure above-water test.
//
//   Height:  the final integer surface height. This is what chunk generation
//            fills solid voxels up to.
//
//   Surface: where the ground actually ended up after carving.
//
//   Water:   the per-column INLAND water surface (rivers, lakes), or NoWater.
// One column of a cascade's sheet: the voxel span the falling water occupies,
// as the run of cells BottomY..TopY inclusive. The drop itself is left as air
// (see HeightMap.Waterfalls), so this is the only record of where the sheet is
// — the ribbon mesh is skinned from the set of them, exactly the way the water
// mesher skins a body of water voxels.
// One metre of the edge water pours over: the column it leaves from and the
// horizontal direction it leaves in. A cascade is a LINE of these, and the sheet
// is swept from them — the fall is a jet leaving a lip, not the block of water
// that would stand in the drop if the drop were filled.
//
// Direction is one of the four axis steps, pointing AWAY from the pool that
// feeds this column, so the sweep knows which way is "out over the edge".
public readonly struct WaterfallLip
{
    public readonly int X;
    public readonly int Z;
    public readonly int DirX;
    public readonly int DirZ;

    public WaterfallLip(int x, int z, int dirX, int dirZ)
    {
        X = x;
        Z = z;
        DirX = dirX;
        DirZ = dirZ;
    }
}

// One cascade: where the water leaves the lip, where it lands, and how wide the
// sheet is. What a waterfall effect needs to place itself, and all a terrain
// approach can honestly say about a drop it routed.
public readonly struct WaterfallSite
{
    // Centre of the sheet in world XZ, at the Y the water pours from.
    public readonly Vector3 Top;
    // Y the sheet lands on — the pool below, or the bed if it lands dry.
    public readonly int BottomY;
    // Columns the sheet spans, so a five-wide fall reads as one wide effect
    // rather than five narrow ones stacked side by side.
    public readonly int Columns;
    // The edge the water actually pours over: the columns of this cascade that
    // touch the pool feeding them, each with the direction it leaves in. The
    // sheet is swept from this line, so it is all the geometry needs — the
    // columns the fall passes THROUGH are not recorded, because a fall is a jet
    // hanging off a lip and not the block of water that would stand in the drop.
    public readonly IReadOnlyList<WaterfallLip> Lips;

    public WaterfallSite(Vector3 top, int bottomY, int columns, IReadOnlyList<WaterfallLip> lips)
    {
        Top = top;
        BottomY = bottomY;
        Columns = columns;
        Lips = lips ?? System.Array.Empty<WaterfallLip>();
    }

    public int Height => Mathf.RoundToInt(Top.Y) - BottomY;
}

public readonly struct HeightMap
{
    // Sentinel in Water for "this column holds no inland water". Below every
    // legal world Y, so callers can fold the global sea in with a plain
    // Math.Max(WorldGen.WATER_LEVEL, GetWaterY(...)) and never special-case it.
    public const int NoWater = int.MinValue;


    public readonly int WorldMinX;
    public readonly int WorldMinZ;
    public readonly int WorldMaxX;
    public readonly int WorldMaxZ;
    public readonly int[,] Plateau;

    // The AUTHORED terrain height: what GenerateChunk fills each column up
    // to, and what Plateau is compared against to decide "flat". Carving
    // does NOT move it — read Surface for where the ground actually is.
    public readonly int[,] Height;

    // The LIVE ground surface: topmost natural terrain voxel in the column.
    // Seeded equal to Height and re-derived by DeriveSurface once carving is
    // done, because a carve can drop a column far below its authored height
    // (GenerateCaves breaches the surface as an open-topped pit on ~10% of
    // columns, worst measured 23 voxels) and every placement pass that
    // anchors to Height would otherwise aim at air. Deliberately ignores
    // architecture — a stamped building does not raise the ground under it,
    // so placement still resolves to the terrain (built ground is kept clear
    // by the separate reservation mask, not by moving the surface).
    public readonly int[,] Surface;

    // Ground an authored builder has claimed — today only subscene
    // footprints (LoadAndReserveSubscenes). Three consumers, and a new
    // builder marking this channel inherits all three: content passes place
    // nothing here, the road pass routes around rather than regrading a
    // building away, and the detail scatter decorates it normally (the
    // stamped ground is real terrain, and its margin should grass over like
    // any other). A channel rather than a per-pass rule so a builder can
    // reserve ground for reasons no geometric test could infer.
    public readonly bool[,] NoSpawn;

    // Topmost INLAND water voxel in the column — a river channel or a lake
    // surface — or NoWater where the column has none. NULL for an approach
    // that makes no inland water at all; GetWaterY then answers NoWater
    // everywhere and every consumer falls back to the global sea plane.
    //
    // Two invariants a producer owes its consumers. The surface must sit on
    // the world's terrain lattice (so a river reads as a flat pool at a
    // terrace level and steps down in whole cascades rather than sloping),
    // and it must be at or above Height for the same column — water is the
    // fill between Height + 1 and here, so a value below the ground means an
    // empty channel. Sea columns are left at NoWater rather than stamped with
    // the sea level: the global waterline already covers them, and folding
    // the two would make every consumer's max() a no-op that hides bugs.
    public readonly int[,] Water;

    // Which way the inland water in this column is MOVING, as a world-XZ
    // vector in the normalized [-1, 1] units ChunkState.SetCurrent stores;
    // zero on a still or dry column. NULL for an approach that routes no
    // water, which GetCurrent answers as zero everywhere.
    //
    // Only water the approach itself routed can carry one — the direction
    // comes from the drainage tree that produced Water, and nothing
    // downstream can re-derive it, because the surface is deliberately FLAT
    // along a reach (see Water above) and so has no gradient to read. Sea
    // columns are left at zero for the same reason they are left at NoWater.
    public readonly Vector2[,] Current;

    // Where this world's cascades are. EMPTY, never null, for an approach that
    // makes none.
    //
    // A LIST of places, not a per-column channel, because nothing consumes a
    // waterfall per column any more: the sheet is drawn by an effect spawned at
    // the site, and it is otherwise plain air that the player falls through. It
    // has to be recorded here because it cannot be re-derived — the drop is air
    // between two pools, which is geometrically identical to a river simply
    // ending at a cliff.
    public readonly IReadOnlyList<WaterfallSite> Waterfalls;


    // The subset of NoSpawn ground a scene opens to AUTHORED placements —
    // a plaza's paving, not a house's floor (SubscenePlacement.allowFixtures).
    // Only the one-off fixture passes consult it; the procedural scatter and
    // the road pass read NoSpawn alone, so they still keep off.
    public readonly bool[,] FixtureGround;

    // The subset of NoSpawn ground a scene opens to the ROAD PASS: the columns
    // around an authored path hint (a front door, a square's gate). Routing
    // alone is exempted — the tread still refuses to stamp a NoSpawn column, so
    // a road can reach a doorway without regrading the floor behind it.
    public readonly bool[,] RoadPortal;

    // The world's vertical lattice, in voxels: the Y multiples that
    // ENCLOSED geometry snaps to — building floors today, cave and tunnel
    // ceilings when they return. Every interior ceiling in the world
    // sitting on a shared grid is what keeps the camera cutaway readable;
    // ceilings at arbitrary heights make it cut through at arbitrary
    // heights. Deliberately independent of how the open-air surface is
    // shaped: the legacy path quantizes terrain to this same step, while
    // the organic path leaves the surface continuous and uses the lattice
    // for interiors alone (its own bench steps vary per region, so they
    // are NOT a lattice anything can anchor to).
    public readonly int LevelStep;

    public HeightMap(int worldMinX, int worldMaxX, int worldMinZ, int worldMaxZ,
        int[,] plateau, int[,] height, int[,] surface, bool[,] noSpawn, int levelStep,
        int[,] water = null, Vector2[,] current = null,
        IReadOnlyList<WaterfallSite> waterfalls = null)
    {
        LevelStep = Math.Max(1, levelStep);
        Water = water;
        Current = current;
        Waterfalls = waterfalls ?? System.Array.Empty<WaterfallSite>();
        WorldMinX = worldMinX;
        WorldMaxX = worldMaxX;
        WorldMinZ = worldMinZ;
        WorldMaxZ = worldMaxZ;
        Plateau = plateau;
        Height = height;
        Surface = surface;
        NoSpawn = noSpawn;
        FixtureGround = new bool[noSpawn.GetLength(0), noSpawn.GetLength(1)];
        RoadPortal = new bool[noSpawn.GetLength(0), noSpawn.GetLength(1)];
    }

    public bool IsNoSpawn(int wx, int wz)
    {
        if (wx < WorldMinX || wx > WorldMaxX || wz < WorldMinZ || wz > WorldMaxZ)
        {
            return false;
        }
        return NoSpawn[wx - WorldMinX, wz - WorldMinZ];
    }

    public void MarkNoSpawn(int wx, int wz)
    {
        if (wx < WorldMinX || wx > WorldMaxX || wz < WorldMinZ || wz > WorldMaxZ)
        {
            return;
        }
        NoSpawn[wx - WorldMinX, wz - WorldMinZ] = true;
    }

    public bool IsFixtureGround(int wx, int wz)
    {
        if (wx < WorldMinX || wx > WorldMaxX || wz < WorldMinZ || wz > WorldMaxZ)
        {
            return false;
        }
        return FixtureGround[wx - WorldMinX, wz - WorldMinZ];
    }

    public void MarkFixtureGround(int wx, int wz)
    {
        if (wx < WorldMinX || wx > WorldMaxX || wz < WorldMinZ || wz > WorldMaxZ)
        {
            return;
        }
        FixtureGround[wx - WorldMinX, wz - WorldMinZ] = true;
    }

    public bool IsRoadPortal(int wx, int wz)
    {
        if (wx < WorldMinX || wx > WorldMaxX || wz < WorldMinZ || wz > WorldMaxZ)
        {
            return false;
        }
        return RoadPortal[wx - WorldMinX, wz - WorldMinZ];
    }

    public void MarkRoadPortal(int wx, int wz)
    {
        if (wx < WorldMinX || wx > WorldMaxX || wz < WorldMinZ || wz > WorldMaxZ)
        {
            return;
        }
        RoadPortal[wx - WorldMinX, wz - WorldMinZ] = true;
    }

    // The three column accessors CLAMP to the map's edge rather than
    // throwing. Placement passes legitimately sample a disc around an
    // anchor — a fixture scatter, a subscene footprint — and a
    // site found at the world edge overhangs it, so "the nearest column" is
    // the right answer and an IndexOutOfRangeException that kills the whole
    // generate is not. IsNoSpawn / MarkNoSpawn already guard the same way.
    public int GetHeight(int wx, int wz)
    {
        ClampToMap(ref wx, ref wz);
        return Height[wx - WorldMinX, wz - WorldMinZ];
    }

    public int GetSurface(int wx, int wz)
    {
        ClampToMap(ref wx, ref wz);
        return Surface[wx - WorldMinX, wz - WorldMinZ];
    }

    public int GetPlateau(int wx, int wz)
    {
        ClampToMap(ref wx, ref wz);
        return Plateau[wx - WorldMinX, wz - WorldMinZ];
    }

    // Inland water surface at this column, or NoWater. Fold the global sea in
    // with Math.Max(WorldGen.WATER_LEVEL, ...) — WorldGen.WaterYAt does exactly
    // that and is what every pass there should call.
    public int GetWaterY(int wx, int wz)
    {
        if (Water == null) { return NoWater; }
        ClampToMap(ref wx, ref wz);
        return Water[wx - WorldMinX, wz - WorldMinZ];
    }

    // Surface current at this column, or zero where there is none.
    public Vector2 GetCurrent(int wx, int wz)
    {
        if (Current == null) { return Vector2.Zero; }
        ClampToMap(ref wx, ref wz);
        return Current[wx - WorldMinX, wz - WorldMinZ];
    }

    private void ClampToMap(ref int wx, ref int wz)
    {
        wx = Math.Clamp(wx, WorldMinX, WorldMaxX);
        wz = Math.Clamp(wz, WorldMinZ, WorldMaxZ);
    }

    public bool IsRamp(int wx, int wz)
    {
        return GetHeight(wx, wz) > GetPlateau(wx, wz);
    }

    // Is this column part of a GRADE (a staircase approximation of a slope)
    // rather than a real discontinuity? Terrain quantizes plateaus to
    // plateauStep voxels, so a genuine plateau edge jumps several voxels at
    // once, while ramps, graded roads and erosion all move at most
    // maxStep per column. That step size — not the apparent angle — is the
    // discriminator: a voxel staircase has no intermediate angles, every
    // adjacent pair is either flat or vertical, so an angle test can't see
    // the slope at all.
    // Tested PER AXIS, not over all four neighbours at once. A ramp climbing
    // the side of a plateau is flanked sideways by the un-ramped plateau, so
    // its cross-slope delta is the full plateau step even though it is
    // unambiguously a grade along its own axis — requiring every neighbour
    // to be gradual hardened the bottom of every such ramp into stairs while
    // leaving the top (where the sideways delta has shrunk to nothing)
    // smooth. An axis qualifies when both its neighbours are within maxStep
    // AND at least one differs: the "differs" clause is what still keeps a
    // plateau edge crisp, since its flat cross-axis is gradual but level.
    public bool IsGrade(int wx, int wz, int maxStep)
    {
        return AxisIsGrade(GetHeight(wx, wz), Delta(wx - 1, wz), Delta(wx + 1, wz), maxStep)
            || AxisIsGrade(GetHeight(wx, wz), Delta(wx, wz - 1), Delta(wx, wz + 1), maxStep);
    }

    // Public so StampGradeShapes can apply the identical rule to the live
    // surface field — the rule must exist in exactly one place.
    public static bool AxisIsGrade(int h, int lo, int hi, int maxStep)
    {
        return Math.Abs(lo - h) <= maxStep
            && Math.Abs(hi - h) <= maxStep
            && (lo != h || hi != h);
    }

    // Neighbour height, clamped into the world so edge columns compare
    // against themselves instead of reading out of bounds.
    private int Delta(int wx, int wz)
    {
        return GetHeight(Math.Clamp(wx, WorldMinX, WorldMaxX), Math.Clamp(wz, WorldMinZ, WorldMaxZ));
    }

}
