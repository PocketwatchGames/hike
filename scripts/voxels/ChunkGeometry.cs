using System.Collections.Generic;
using Godot;

// Everything building one chunk produces that is NOT a Godot object — the
// output of the pure half of ChunkMesh.Create.
//
// The split exists because ~98% of a chunk build is CPU geometry (the DC
// mesher, the water mesher, the ledge barriers, the detail scatter) and ~2% is
// the handful of rendering-server calls that turn it into nodes. Only the
// second half has to be on the main thread, so the first can be run for many
// chunks at once — which is what the initial world fill does. See
// ChunkMesh.BuildGeometry / ChunkMesh.Realize.
//
// Nothing in here may hold a Node, a Resource, or an RID: the whole point is
// that a worker thread can produce one.
public sealed class ChunkGeometry
{
    public ChunkState Data;
    public Vector3I ChunkCoord;

    // Terrain surface, 4 CUSTOM channels. Empty when the chunk meshed to nothing.
    public MeshBuffer Terrain;
    public bool HasTerrain;

    // Water surface, 1 CUSTOM channel.
    public MeshBuffer Water;
    public bool HasWater;

    // Ledge-barrier triangles in CHUNK-LOCAL space, one entry per
    // LedgeBarrierClasses.All index (null where that class has no ledges in this
    // chunk). Already the plain vertex soup ConcavePolygonShape3D wants.
    public Vector3[][] LedgeBarrierTris;

    // Detail-sprite instances this chunk contributes, keyed by entry. Posted to
    // the world-wide scatter manager during Realize — that manager owns
    // MultiMeshes, so the post itself is main-thread.
    public Dictionary<DetailEntry, List<ChunkDetailScatter.InstanceData>> Scatter;
    public bool WantsScatter;

    // Carried through from the load decision, since Realize needs them and
    // re-deriving them would mean knowing the player's chunk out here.
    public bool BuildCollision;
    public bool OutOfLightWindow;

    // True when the chunk had no geometry at all and Realize has only to hand
    // back an empty, collision-ready node.
    public bool IsEmpty => !HasTerrain && !HasWater;
}
