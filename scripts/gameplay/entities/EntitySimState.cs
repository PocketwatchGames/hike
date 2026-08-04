using System;
using Godot;

// Persistent simulation state for an entity. Lives in WorldState across save/load
// and outlives the spawned Node3D — when a chunk unloads, the node is freed but
// this state stays so the entity can be re-materialized later with its current
// values intact. Mover-type entities (Mob, Loot) are expected to write their
// current position back into WorldPosition before being freed.
public abstract class EntitySimState
{
    // Mutable so movers can sync their current position back before unload.
    // Non-movers leave it equal to the spawn position.
    public Vector3 WorldPosition;
    // Y-axis facing, radians. Every entity carries one so the editor's rotate
    // gizmo works on anything selectable; movers (mobs) also write their live
    // facing back here before unload.
    public float RotationY;

    // Subscene variant pool this entity belongs to. Empty (the default) means
    // unconditional: the entity spawns wherever its scene is stamped. A tagged
    // entity is a CANDIDATE — it spawns only when the stamping variant selects
    // its pool and the roll picks this position, so tagging something makes it
    // stop spawning everywhere that doesn't ask for it. On a MarkerSimState the
    // tag is all there is: the pool name for a position with no authored body.
    public string Tag = "";
    public readonly PackedScene Scene;

    protected EntitySimState(Vector3 worldPosition, PackedScene scene)
    {
        WorldPosition = worldPosition;
        Scene = scene;
    }

    // Seats a freshly instantiated node on this state's transform. Call it in
    // CreateEntity BEFORE AddChild — several entities read their transform in
    // _Ready, so seating afterwards is too late. Only the Y rotation is
    // written, so a scene root authored with an X/Z tilt keeps it.
    public void SeatTransform(Node3D node)
    {
        node.Position = WorldPosition;
        Vector3 rotation = node.Rotation;
        node.Rotation = new Vector3(rotation.X, RotationY, rotation.Z);
    }

    // Turns this state's authored transform by `quarterTurns` × 90° about +Y,
    // for a subscene stamped at a rotation (see SubsceneRotator). `mapPosition`
    // carries the whole position mapping — the turn plus the re-origining that
    // keeps the scene's box at its corner — so an override never does the math
    // itself, it just routes each position it owns through the map. The base
    // covers WorldPosition and RotationY; override for any OTHER position or
    // facing a state stores, and call base.
    //
    // Nothing that derives its shape from RotationY needs an override: a roof's
    // footprint, a door's occluder column and every seated model already read
    // the rotation back out.
    public virtual void RotateQuarterTurns(int quarterTurns, Func<Vector3, Vector3> mapPosition)
    {
        WorldPosition = mapPosition(WorldPosition);
        RotationY = Mathf.Wrap(RotationY + quarterTurns * Mathf.Pi * 0.5f, -Mathf.Pi, Mathf.Pi);
    }

    // Returns null if this sim state should not materialize an entity right now
    // (e.g. picked up loot, dead mob).
    public abstract Node3D CreateEntity(Sim sim);

    // True if a node should be CREATED for this state right now. This is a
    // spawn gate only — not a presence gate. World checks it when loading a
    // chunk and again at sunset for already-active chunks so night-only
    // entities can appear without a chunk reload. Once a node exists it
    // stays alive until the chunk evicts (or gameplay frees it); World
    // does not despawn it just because ShouldSpawn flips back to false.
    public virtual bool ShouldSpawn(Sim sim) => true;

    // Live node reference, set by World when the entity is materialized and
    // cleared via TreeExiting when it leaves the scene. Used by the
    // day/night refresh pass to find which states currently have a node.
    // Not serialized — purely runtime bookkeeping.
    public Node3D RuntimeNode;

    // Voxel cells this entity occupies for the purposes of mob pathfinding.
    // World refcount-registers each cell on spawn and decrements on
    // TreeExiting, so overlapping props (e.g. a chest tucked against a tree)
    // keep the union of their cells blocked until the last entity leaves.
    // The walkability sampler treats any surface column whose stand-in cells
    // are blocked as unwalkable. Default: emit nothing — only entities with
    // a meaningful physical footprint should override. `entity` is the live
    // runtime node, so shape-derived implementations (e.g. trees rasterizing
    // their cylinder collider) can read its CollisionShape3D directly.
    public virtual void GetPathBlockerCells(Node3D entity, System.Collections.Generic.List<Vector3I> outCells) { }

    // Radius (meters) of the damaging "danger zone" around this entity that
    // mobs avoid when wandering and never spawn inside. 0 = harmless (the
    // default). Set by the spawning code (fire trap / campfire / spike trap)
    // from authored tuning. Distinct from GetPathBlockerCells: a hazard cell
    // is still walkable (a chasing mob can be lured across it) — it's only
    // routed-around by wander/normal pathing, whereas a path-blocker cell is
    // impassable to everyone. World refcounts the disc of cells this radius
    // covers into its hazard grid on spawn (see Sim.RegisterEntity).
    public float HazardRadius;

    // True if this is natural surface scenery a road should avoid and clear:
    // WorldGen's road pass adds pathfinding cost for these in its R×R window (so
    // roads thread through open ground) and deletes any that sit on the tread it
    // carves. Set on trees, tall grass, climbable / berry trees. Gameplay
    // entities and intentional landmarks (mobs, loot, chests, signposts, wells,
    // campfires, knowledge stones) leave it false so roads never delete them.
    public virtual bool IsRoadObstacle => false;

    // True if this entity was placed by an authored fixture pass (zone / region
    // / POI fixtures), as opposed to procedural scatter. WorldGen's road pass
    // routes AROUND these and never clears or regrades under them, so a road
    // can't bulldoze a campfire, well, signpost, or any authored landmark.
    // Stamped by WorldState.AddEntity while WorldState.TaggingFixtures is set.
    public bool PlacedAsFixture;
}
