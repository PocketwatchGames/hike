using System.Collections.Generic;
using Godot;

// Per-cell refcount of pathfinding blockers contributed by spawned entities
// (trees, chests, etc.). Refcounted so multiple entities sharing a cell — or
// lifetime overlap during respawn — don't drop the block prematurely. Queried
// by WalkabilityGrid.SampleColumn so mobs route around props the voxel grid
// alone can't see.
//
// Owned by World (one shared index, like MobSpatialHash); each blocker entity
// adds 1 to every cell it occupies on spawn and removes 1 on TreeExiting.
public class PathBlockerGrid
{
    private readonly Dictionary<Vector3I, int> _cells = new();

    public void Add(Vector3I cell)
    {
        _cells.TryGetValue(cell, out int count);
        _cells[cell] = count + 1;
    }

    public void Remove(Vector3I cell)
    {
        if (!_cells.TryGetValue(cell, out int count))
        {
            return;
        }
        if (count <= 1)
        {
            _cells.Remove(cell);
        }
        else
        {
            _cells[cell] = count - 1;
        }
    }

    public bool IsBlocked(int wx, int wy, int wz)
    {
        return _cells.ContainsKey(new Vector3I(wx, wy, wz));
    }
}
