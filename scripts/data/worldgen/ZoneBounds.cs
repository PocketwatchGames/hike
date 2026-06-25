using System;
using Godot;

// Per-world placement boundary for a zone — the data-driven replacement for the
// hardcoded quadrant split + hub carve in WorldGen.PickZoneIndex. A PlacedZone
// pairs one of these with a reusable ZoneGenData template; WorldGen assigns each
// chunk to the highest-Priority bounds whose Contains() is true.
//
// Bounds live on the PlacedZone container (not on ZoneGenData) so zone templates
// stay location-free and reusable across worlds. Subclass per shape
// (EverywhereBounds, QuadrantBounds, BoxBounds, CircleBounds).
[GlobalClass]
public partial class ZoneBounds : Resource
{
    // Highest-priority matching bounds wins where several overlap a chunk. A
    // background fill (EverywhereBounds) sits at 0; insets override it with a
    // higher value.
    [Export] public int Priority;

    // How far (in chunks) this zone's surface content blends across its border.
    // The surface spawn pass places content from each column's DOMINANT zone by
    // default (0 = a hard, clean boundary — nothing from a neighbour bleeds in,
    // so a settlement never inherits a wild biome's mobs). A positive value
    // softens the seam: the pass instead does a kernel-weighted roll over this
    // reach, so this zone's content (and its neighbours') interleave that many
    // chunks past the boundary — the natural look for two wild biomes meeting.
    [Export] public float SpawnBlendReachChunks = 0f;

    // How far (in chunks) terrain elevation feathers at this zone's border when
    // it's the dominant zone. 0 = use the world's global ZoneGenBlendRadius (the
    // default soft transition). A small value tightens the edge so this zone's
    // terrain holds across its whole footprint without a neighbour's elevation
    // bleeding in — e.g. the village stays a flat, dry beach right up to its rim
    // instead of the swamp's underwater terrain dipping a pond inside it.
    [Export] public float TerrainBlendChunks = 0f;

    // True iff this zone claims the chunk at (chunkX, chunkZ). Base returns
    // false (claims nothing); subclasses override.
    public virtual bool Contains(int chunkX, int chunkZ, in ZoneBoundsContext ctx)
    {
        return false;
    }

    // Fixed anchor chunk for this zone's one-off Fixtures cluster (a box/circle
    // center). Returns false when the shape has no natural center — the caller
    // then rolls a random flat-dry column inside the bounds instead.
    public virtual bool TryGetAnchorChunk(in ZoneBoundsContext ctx, out Vector2I chunk)
    {
        chunk = default;
        return false;
    }
}

// Per-run inputs threaded into ZoneBounds evaluation. Built once at the top of
// WorldGen.Generate and reused for every chunk pick. EdgeNoise returns a smooth
// value in [-1, 1] at chunk coordinates, used by box/circle bounds to wobble
// their borders so a zone melts organically into its neighbor instead of
// snapping at a clean geometric edge.
public readonly struct ZoneBoundsContext
{
    public readonly Vector3I WorldChunkMin;
    public readonly Vector3I WorldChunkMax;
    public readonly Vector2I SpawnChunk;
    public readonly Func<int, int, float> EdgeNoise;

    public ZoneBoundsContext(Vector3I worldChunkMin, Vector3I worldChunkMax,
        Vector2I spawnChunk, Func<int, int, float> edgeNoise)
    {
        WorldChunkMin = worldChunkMin;
        WorldChunkMax = worldChunkMax;
        SpawnChunk = spawnChunk;
        EdgeNoise = edgeNoise;
    }

    public float SampleEdgeNoise(int chunkX, int chunkZ)
    {
        return EdgeNoise != null ? EdgeNoise(chunkX, chunkZ) : 0f;
    }
}
