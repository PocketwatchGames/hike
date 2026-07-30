using System.Collections.Generic;
using Godot;

// The entity tool's current selection.
//
// Holds EntitySimStates rather than nodes: committing a move respawns the
// affected chunks' entity nodes, so a node reference would dangle after the
// first drag while the state survives it.
public sealed class EditorEntitySelection
{
    private readonly List<EntitySimState> _states = new List<EntitySimState>();

    public IReadOnlyList<EntitySimState> States => _states;
    public int Count => _states.Count;
    public bool IsEmpty => _states.Count == 0;

    public bool Contains(EntitySimState state)
    {
        return state != null && _states.Contains(state);
    }

    public void Clear()
    {
        _states.Clear();
    }

    public void SetSingle(EntitySimState state)
    {
        _states.Clear();
        if (state != null)
        {
            _states.Add(state);
        }
    }

    // Shift-click: into the group if it wasn't in it, out if it was.
    public void Toggle(EntitySimState state)
    {
        if (state == null)
        {
            return;
        }
        if (!_states.Remove(state))
        {
            _states.Add(state);
        }
    }

    // Midpoint of the selection — the gizmo's pivot, and what a rotate drag
    // orbits everything around.
    public Vector3 Pivot
    {
        get
        {
            if (_states.Count == 0)
            {
                return Vector3.Zero;
            }
            Vector3 sum = Vector3.Zero;
            foreach (EntitySimState state in _states)
            {
                sum += state.WorldPosition;
            }
            return sum / _states.Count;
        }
    }

    // Drops states the world no longer holds — after a delete, or after an undo
    // swapped a chunk's bucket for a different set of objects. Only the paths
    // that can remove a state call this; chunk eviction frees nodes but leaves
    // the states filed, so there is nothing to prune per frame.
    public void Prune(WorldState world)
    {
        _states.RemoveAll(state => !IsFiled(world, state));
    }

    // Sweeps the neighbouring buckets too: a state isn't necessarily filed under
    // the chunk its position maps to (a mob saved mid-walk, an entity between a
    // move and its re-file), and pruning one of those would silently drop it out
    // of the selection.
    private static bool IsFiled(WorldState world, EntitySimState state)
    {
        if (state == null)
        {
            return false;
        }
        Vector3I center = Sim.WorldToChunkCoord(state.WorldPosition);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    List<EntitySimState> bucket = world.GetEntities(center + new Vector3I(dx, dy, dz));
                    if (bucket != null && bucket.Contains(state))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }
}
