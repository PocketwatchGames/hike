using Godot;
using System.Collections.Generic;

// One-time setup of the invisible StaticBody3D walls + floor that box the
// streamed world in, so the player and physics bodies can't fall off the edge
// or out the bottom. Pure construction with no ongoing state — World calls
// Create once from Initialize.
public static class WorldBoundary
{
    private const float WALL_THICKNESS = 1f;

    // Returns the bodies' RIDs, for queries that must not see them. They sit on
    // Environment like real terrain but are invisible and outside the world, so
    // anything picking a point to act on (the editor's brush) has to skip them.
    public static List<Rid> Create(Node3D parent, WorldState worldState)
    {
        Vector3 minWorld = new Vector3(
            worldState.Min.X * ChunkState.SIZE,
            worldState.Min.Y * ChunkState.SIZE,
            worldState.Min.Z * ChunkState.SIZE
        );
        Vector3 maxWorld = new Vector3(
            (worldState.Max.X + 1) * ChunkState.SIZE,
            (worldState.Max.Y + 1) * ChunkState.SIZE,
            (worldState.Max.Z + 1) * ChunkState.SIZE
        );
        Vector3 center = (minWorld + maxWorld) / 2f;
        Vector3 size = maxWorld - minWorld;

        float wallHeight = size.Y;

        var rids = new List<Rid>();

        // North wall (+Z)
        rids.Add(AddWall(parent, new Vector3(center.X, center.Y, maxWorld.Z + WALL_THICKNESS / 2f),
            new Vector3(size.X + WALL_THICKNESS * 2f, wallHeight, WALL_THICKNESS)));

        // South wall (-Z)
        rids.Add(AddWall(parent, new Vector3(center.X, center.Y, minWorld.Z - WALL_THICKNESS / 2f),
            new Vector3(size.X + WALL_THICKNESS * 2f, wallHeight, WALL_THICKNESS)));

        // East wall (+X)
        rids.Add(AddWall(parent, new Vector3(maxWorld.X + WALL_THICKNESS / 2f, center.Y, center.Z),
            new Vector3(WALL_THICKNESS, wallHeight, size.Z + WALL_THICKNESS * 2f)));

        // West wall (-X)
        rids.Add(AddWall(parent, new Vector3(minWorld.X - WALL_THICKNESS / 2f, center.Y, center.Z),
            new Vector3(WALL_THICKNESS, wallHeight, size.Z + WALL_THICKNESS * 2f)));

        // Floor (-Y)
        rids.Add(AddWall(parent, new Vector3(center.X, minWorld.Y - WALL_THICKNESS / 2f, center.Z),
            new Vector3(size.X + WALL_THICKNESS * 2f, WALL_THICKNESS, size.Z + WALL_THICKNESS * 2f)));

        return rids;
    }

    private static Rid AddWall(Node3D parent, Vector3 position, Vector3 size)
    {
        var body = new StaticBody3D();
        body.Position = position;

        var shape = new BoxShape3D();
        shape.Size = size;

        var collisionShape = new CollisionShape3D();
        collisionShape.Shape = shape;

        body.AddChild(collisionShape);
        parent.AddChild(body);
        return body.GetRid();
    }
}
