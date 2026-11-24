using System;
using System.Collections.Generic;
using Godot;

// A bomb wall (BombWallSpawnEntry): the world cells that give way, and whether
// they have. Breaking is permanent, so a broken wall spawns no node but keeps its
// state — it is what re-clears the cells when a save loads over the world file's
// standing rock (SaveFile.ReconcileStamps).
public class BombWallSimState : EntitySimState, IVoxelStamper
{
    public readonly BlockSurfaceData CrackSurface;
    // World cells, absolute — resolved from the placement's offsets and facing at
    // spawn, so nothing downstream has to know about the placement frame.
    public Vector3I[] Cells;
    public bool Broken;

    public BombWallSimState(Vector3 worldPosition, float rotationY, PackedScene scene,
        BlockSurfaceData crackSurface, Vector3I[] cells)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
        CrackSurface = crackSurface;
        Cells = cells ?? Array.Empty<Vector3I>();
    }

    public override bool ShouldSpawn(Sim sim) => !Broken;

    public override Node3D CreateEntity(Sim sim)
    {
        return Broken ? null : BombWall.Create(sim, this);
    }

    // The wall IS voxels; navigation reads those directly.
    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells) { }

    // Standing: the rock as it is, dressed in the crack overlay on its open faces.
    // Broken: Air, carving whatever rock is there.
    public VoxelStamp ResolveStamp(WorldState world)
    {
        if (Broken)
        {
            return new VoxelStamp(Cells, Blocks.AirId, carves: true);
        }
        int overlay = CrackSurface?.atlasBaseIndex ?? ChunkState.OVERLAY_NONE;
        return new VoxelStamp(Cells, VoxelStamp.KeepBlock, carves: false, overlayId: overlay);
    }

    public override void RotateQuarterTurns(int quarterTurns, Func<Vector3, Vector3> mapPosition)
    {
        base.RotateQuarterTurns(quarterTurns, mapPosition);
        var half = new Vector3(0.5f, 0.5f, 0.5f);
        for (int i = 0; i < Cells.Length; i++)
        {
            Vector3 mapped = mapPosition(Cells[i] + half);
            Cells[i] = new Vector3I(Mathf.FloorToInt(mapped.X), Mathf.FloorToInt(mapped.Y), Mathf.FloorToInt(mapped.Z));
        }
    }

    // The world box the cells span, or Empty.
    public VoxelBox Bounds()
    {
        if (Cells.Length == 0)
        {
            return VoxelBox.Empty;
        }
        Vector3I min = Cells[0];
        Vector3I max = Cells[0];
        foreach (Vector3I cell in Cells)
        {
            min = min.Min(cell);
            max = max.Max(cell);
        }
        return new VoxelBox(min, max);
    }
}
