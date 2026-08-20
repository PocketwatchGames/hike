using System.Collections.Generic;
using Godot;

// One reversible family of painted-document state (raster tiles, the tunnel
// mask, the placement list) that knows how to snapshot and restore itself.
//
// Deliberately the same contract as the world editor's IEditorEditAspect, for
// the same reasons — snapshot on touch, capture "after" at commit, drop an
// aspect that did not change — but a separate hierarchy because none of the
// state overlaps: that one moves voxels and entities in a WorldState, this one
// moves pixels in layer images.
//
// To make a new KIND of painted state undoable: implement this, then add a
// lazily-created field plus a Touch* method on MapEdit. No tool is involved.
public interface IMapEditAspect
{
    // Called once at commit. False = nothing this aspect covers actually
    // changed, so it costs nothing to keep.
    bool CaptureAfter(WorldMapState ctx);

    void Restore(WorldMapState ctx, bool redo);
}

// A single undoable painter action — one brush stroke (press through release),
// one stamp placed, one rotation.
//
// The host brackets the stroke and touches the region the brush covers, so
// NO TOOL WRITES UNDO LOGIC and no tool can forget to: a new tool is undoable
// the moment it is added to the list. The cost of not knowing which layer a tool
// writes is that every layer over that region is snapshotted; the cost of that
// is paid back at commit, where each aspect drops what did not change, so a
// stroke that moved one layer keeps one layer.
public sealed class MapEdit
{
    public readonly string Name;

    private readonly List<IMapEditAspect> _aspects = new List<IMapEditAspect>();
    private RasterTilesAspect _rasters;
    private TunnelTilesAspect _tunnels;
    private PlacementsAspect _placements;

    public MapEdit(string name)
    {
        Name = name;
    }

    // Declares a texel region this edit is about to overwrite. Must be called
    // BEFORE the write, which is the only time the old values are readable.
    // Called repeatedly as a drag wanders; a tile already captured is skipped,
    // so the snapshot grows with the area painted rather than with the number of
    // motion events.
    public void TouchRect(WorldMapState ctx, Rect2I texelRect)
    {
        _rasters ??= Add(new RasterTilesAspect());
        _rasters.Touch(ctx, texelRect);
        _tunnels ??= Add(new TunnelTilesAspect());
        _tunnels.Touch(ctx, texelRect);
    }

    // The placement list is small and edited as a whole (an entry added,
    // removed, moved or turned), so it is snapshotted whole rather than tiled.
    public void TouchPlacements(WorldMapState ctx)
    {
        _placements ??= Add(new PlacementsAspect());
        _placements.Touch(ctx);
    }

    // True if anything actually moved. False means the edit can be dropped
    // rather than costing an undo slot — which is what makes it safe for the
    // host to open an edit on every press, including presses that paint nothing.
    public bool CaptureAfter(WorldMapState ctx)
    {
        bool any = false;
        for (int i = _aspects.Count - 1; i >= 0; i--)
        {
            if (_aspects[i].CaptureAfter(ctx))
            {
                any = true;
            }
            else
            {
                _aspects.RemoveAt(i);
            }
        }
        return any;
    }

    public void Undo(WorldMapState ctx)
    {
        foreach (IMapEditAspect aspect in _aspects)
        {
            aspect.Restore(ctx, false);
        }
    }

    public void Redo(WorldMapState ctx)
    {
        foreach (IMapEditAspect aspect in _aspects)
        {
            aspect.Restore(ctx, true);
        }
    }

    private T Add<T>(T aspect) where T : IMapEditAspect
    {
        _aspects.Add(aspect);
        return aspect;
    }
}
