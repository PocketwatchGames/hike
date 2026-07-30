using System.Collections.Generic;
using Godot;

// One reversible family of world state (voxel cells, entity buckets, ...) that
// knows how to snapshot and restore itself.
//
// The contract is snapshot-on-touch: a tool declares WHAT it is about to change
// before it writes, the aspect captures the "before" then and the "after" at
// commit. No tool ever writes undo logic, and a tool that reaches into
// WorldState in some new way still undoes correctly as long as it touched what
// it wrote — which is what lets a bulk helper like SubsceneStamper be undone
// without knowing anything about the editor.
//
// To make a new KIND of state undoable: implement this, then add a
// lazily-created field plus a Touch* method on EditorEdit.
public interface IEditorEditAspect
{
    // Called once at commit. False = nothing this aspect covers actually
    // changed, so the edit can be dropped instead of costing an undo slot.
    bool CaptureAfter(WorldState world);

    // Writes the captured "before" (undo) or "after" (redo) back into the
    // world, recording what the live scene has to refresh.
    void Restore(WorldState world, bool redo, EditorRefresh refresh);
}

// A single undoable editor action — one brush stroke (press through release),
// one entity placement, one subscene stamp. Tools touch what they are about to
// change; EditorHistory opens, commits and replays these.
public sealed class EditorEdit
{
    // Printed when the edit is undone or redone.
    public readonly string Name;

    private readonly WorldState _world;
    private readonly List<IEditorEditAspect> _aspects = new List<IEditorEditAspect>();

    private VoxelCellsAspect _voxels;
    private ChunkFieldsAspect _chunkFields;
    private EntityBucketsAspect _entities;
    private EntityTransformsAspect _entityTransforms;
    private SpawnAspect _spawn;

    public EditorEdit(string name, WorldState world)
    {
        Name = name;
        _world = world;
    }

    // Declares a voxel cell this edit is about to overwrite. Must be called
    // BEFORE the write — that's when the old value is still readable.
    //
    // Also snapshots the owning chunk's whole-chunk fields. They're a few
    // hundred bytes per chunk and tools that write them (a subscene stamp's env
    // overrides) always write voxels alongside, so folding them in here means
    // no tool has to remember them.
    public void TouchVoxel(Vector3I cell)
    {
        _voxels ??= Add(new VoxelCellsAspect());
        _voxels.Touch(_world, cell);
        TouchChunkFields(Sim.WorldToChunkCoord(cell));
    }

    public void TouchVoxels(List<Vector3I> cells)
    {
        foreach (Vector3I cell in cells)
        {
            TouchVoxel(cell);
        }
    }

    // Whole-chunk fields only — zone/region index and the coarse env subgrids.
    // Rarely needed on its own; TouchVoxel covers the usual case.
    public void TouchChunkFields(Vector3I chunkCoord)
    {
        _chunkFields ??= Add(new ChunkFieldsAspect());
        _chunkFields.Touch(_world, chunkCoord);
    }

    // Declares that the set of entities filed in a chunk is about to change —
    // an add, a delete, or a bulk stamp. The whole bucket is snapshotted, so the
    // caller never has to say which entity or how many.
    public void TouchEntityChunk(Vector3I chunkCoord)
    {
        _entities ??= Add(new EntityBucketsAspect());
        _entities.Touch(_world, chunkCoord);
    }

    public void TouchEntitiesAt(Vector3 worldPosition)
    {
        TouchEntityChunk(Sim.WorldToChunkCoord(worldPosition));
    }

    // Declares that an already-placed entity is about to be moved or rotated.
    // Its current chunk is touched too, so a move that crosses a chunk boundary
    // restores its old bucket membership as well as its old transform.
    public void TouchEntityTransform(EntitySimState state)
    {
        if (state == null)
        {
            return;
        }
        _entityTransforms ??= Add(new EntityTransformsAspect());
        _entityTransforms.Touch(state);
        TouchEntitiesAt(state.WorldPosition);
    }

    public void TouchSpawn()
    {
        _spawn ??= Add(new SpawnAspect());
        _spawn.Touch(_world);
    }

    // False when the action turned out to be a no-op (a click that painted the
    // material already there, a drag that never left the clipped band).
    public bool CaptureAfter()
    {
        bool changed = false;
        foreach (IEditorEditAspect aspect in _aspects)
        {
            // Every aspect must run — this prunes their unchanged entries too,
            // so it can't be short-circuited on the first `true`.
            changed = aspect.CaptureAfter(_world) || changed;
        }
        return changed;
    }

    public void Undo(EditorRefresh refresh)
    {
        Restore(false, refresh);
    }

    public void Redo(EditorRefresh refresh)
    {
        Restore(true, refresh);
    }

    private void Restore(bool redo, EditorRefresh refresh)
    {
        foreach (IEditorEditAspect aspect in _aspects)
        {
            aspect.Restore(_world, redo, refresh);
        }
    }

    private T Add<T>(T aspect) where T : IEditorEditAspect
    {
        _aspects.Add(aspect);
        return aspect;
    }
}
