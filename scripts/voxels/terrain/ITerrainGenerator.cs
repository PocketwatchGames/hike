// One run of one terrain approach. `WorldGen` builds exactly one of these per
// generate (from the authored TerrainGenData) and drives it through the three
// hooks below; everything else in worldgen — kits, roads, props, spawns, fog,
// lighting — is approach-agnostic and reads only the HeightMap that comes out.
//
// The split between this and TerrainGenData follows the project's Data/runtime
// rule: TerrainGenData is the authored, immutable, shared tuning; the generator
// is the per-run object that owns the noise channels seeded from this world's
// seed. Nothing here is stored on the resource, so two worlds generating
// concurrently from one .tres cannot tread on each other.
//
// ADDING AN APPROACH: write a TerrainGenData subclass carrying its knobs, and
// an ITerrainGenerator implementation in its own file under scripts/voxels/
// terrain/. Point the subclass's CreateGenerator at it. Touch nothing else —
// in particular, do not add fields to WorldGenData, which is what let the first
// two approaches bleed into each other.
using System.Collections.Generic;
using Godot;

public interface ITerrainGenerator
{
    // Build the world's height field. Called once, before any chunk exists.
    HeightMap BuildHeightMap(WorldState ws);

    // Is this voxel carved out, despite sitting at or below its column's solid
    // height? Called per solid voxel candidate during chunk fill, so it is the
    // hook for approaches that hollow terrain as they generate it (the plateau
    // approach's tunnel slabs). Return false to leave the column solid.
    //
    // MUST be a pure function of its arguments: chunk fill order is not
    // guaranteed, and the same voxel is queried again as a neighbour when the
    // mesher decides surface shapes.
    bool IsCarvedAt(int wx, int wy, int wz, int columnSolidHeight);

    // Must this CARVED voxel be left as air even though it sits at or below its
    // column's waterline? Chunk fill floods carved voxels under the waterline,
    // which is right for anything open to the sky or the sea — the channel under
    // a bridge deck — and wrong for an enclosed one. A cave sealed by rock on
    // every side, whose only opening is above the water, stays dry however far
    // it descends; without this every passage below sea level fills to its
    // ceiling. Only asked about voxels IsCarvedAt already claimed, and it must
    // be pure for the same reason IsCarvedAt is. Default: nothing is sealed, so
    // an approach that carves nothing under water need not implement it.
    bool IsSealedFromWaterAt(int wx, int wy, int wz);

    // Carve volumes that need the finished voxel grid rather than a single
    // column (the plateau approach's caves). Called after every chunk is
    // filled and before the surface is re-derived. Default: carve nothing.
    void CarveVolumes(WorldState ws);

    // Landforms this approach placed and named, for WorldState.PointsOfInterest
    // — a named mesa becomes a place the road pathfinder routes to and a
    // signpost names, off registries that already exist. Names are stable
    // internal identifiers, never shown to the player, and an approach that
    // places nothing named returns an empty list.
    IReadOnlyList<KeyValuePair<string, Vector3>> GetNamedFeatures();

    // Write whatever this approach's own dump needs into `dir`, alongside the
    // approach-agnostic images DumpDebug writes. The hook exists because the
    // shared dump is a HEIGHTFIELD view and cannot show anything an approach
    // carves — a hillshade of a world with caves under it is identical to one
    // without. Default: write nothing.
    void DumpDiagnostics(string dir);
}
