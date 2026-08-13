using System;
using System.Collections.Generic;
using Godot;

// Every authored channel of one voxel cell. When a new per-voxel channel
// becomes editable, add it here and every editor tool's undo picks it up —
// this struct is the only place voxel state is snapshotted.
public readonly struct VoxelCellState : IEquatable<VoxelCellState>
{
    public readonly int Type;
    public readonly SharpAxes Shape;
    public readonly byte TerrainId;
    public readonly byte OverlayId;
    public readonly byte DetailGroup;
    public readonly byte DetailStrength;

    private VoxelCellState(int type, SharpAxes shape, byte terrainId, byte overlayId, byte detailGroup, byte detailStrength)
    {
        Type = type;
        Shape = shape;
        TerrainId = terrainId;
        OverlayId = overlayId;
        DetailGroup = detailGroup;
        DetailStrength = detailStrength;
    }

    public static VoxelCellState Capture(WorldState world, Vector3I cell)
    {
        return new VoxelCellState(
            world.GetBlockWorld(cell.X, cell.Y, cell.Z),
            world.GetShapeWorld(cell.X, cell.Y, cell.Z),
            (byte)world.GetTerrainIdWorld(cell.X, cell.Y, cell.Z),
            (byte)world.GetOverlayIdWorld(cell.X, cell.Y, cell.Z),
            (byte)world.GetDetailGroupWorld(cell.X, cell.Y, cell.Z),
            (byte)world.GetDetailStrengthWorld(cell.X, cell.Y, cell.Z));
    }

    public void ApplyTo(WorldState world, Vector3I cell)
    {
        // The shape-less SetBlockWorld overload substitutes the material's
        // default shape whenever the material changes, which is right for a
        // brush and wrong for restoring an exact snapshot.
        world.SetBlockWorld(cell.X, cell.Y, cell.Z, Type, Shape);
        world.SetTerrainIdWorld(cell.X, cell.Y, cell.Z, TerrainId);
        world.SetOverlayIdWorld(cell.X, cell.Y, cell.Z, OverlayId);
        world.SetDetailGroupWorld(cell.X, cell.Y, cell.Z, DetailGroup);
        world.SetDetailStrengthWorld(cell.X, cell.Y, cell.Z, DetailStrength);
    }

    public bool Equals(VoxelCellState other)
    {
        return Type == other.Type
            && Shape == other.Shape
            && TerrainId == other.TerrainId
            && OverlayId == other.OverlayId
            && DetailGroup == other.DetailGroup
            && DetailStrength == other.DetailStrength;
    }
}

// Per-cell voxel state. The workhorse aspect: every voxel brush, fill, stamp
// and future terrain tool records through this one.
public sealed class VoxelCellsAspect : IEditorEditAspect
{
    private Dictionary<Vector3I, VoxelCellState> _before = new Dictionary<Vector3I, VoxelCellState>();
    private readonly Dictionary<Vector3I, VoxelCellState> _after = new Dictionary<Vector3I, VoxelCellState>();

    public void Touch(WorldState world, Vector3I cell)
    {
        // First touch wins: a drag that sweeps back over a cell must restore
        // what was there before the STROKE, not before the last dab.
        if (!_before.ContainsKey(cell))
        {
            _before[cell] = VoxelCellState.Capture(world, cell);
        }
    }

    public bool CaptureAfter(WorldState world)
    {
        // Cells a tool touched but left alone — clipped away, or painted with
        // the material already there — are dropped rather than kept as
        // no-op entries that cost memory and re-meshes on every undo.
        var changed = new Dictionary<Vector3I, VoxelCellState>(_before.Count);
        foreach (KeyValuePair<Vector3I, VoxelCellState> kvp in _before)
        {
            VoxelCellState now = VoxelCellState.Capture(world, kvp.Key);
            if (now.Equals(kvp.Value))
            {
                continue;
            }
            changed[kvp.Key] = kvp.Value;
            _after[kvp.Key] = now;
        }
        _before = changed;
        return _after.Count > 0;
    }

    public void Restore(WorldState world, bool redo, EditorRefresh refresh)
    {
        Dictionary<Vector3I, VoxelCellState> target = redo ? _after : _before;
        foreach (KeyValuePair<Vector3I, VoxelCellState> kvp in target)
        {
            kvp.Value.ApplyTo(world, kvp.Key);
            refresh.AddVoxel(kvp.Key);
        }
    }
}
