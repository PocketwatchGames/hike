using System.Collections.Generic;
using Godot;

// The entity tool's current selection.
//
// Holds PLACEMENTS rather than states or nodes: a move or a retune respawns a
// placement's states, so anything that pointed at those would dangle after the
// first drag, while the placement is the thing being edited and survives it.
public sealed class EditorEntitySelection
{
    private readonly List<SceneEntityPlacement> _placements = new List<SceneEntityPlacement>();

    public IReadOnlyList<SceneEntityPlacement> Placements => _placements;
    public int Count => _placements.Count;
    public bool IsEmpty => _placements.Count == 0;

    public void Clear()
    {
        _placements.Clear();
    }

    public void SetSingle(SceneEntityPlacement placement)
    {
        _placements.Clear();
        if (placement != null)
        {
            _placements.Add(placement);
        }
    }

    public void SetMany(IEnumerable<SceneEntityPlacement> placements)
    {
        _placements.Clear();
        foreach (SceneEntityPlacement placement in placements)
        {
            if (placement != null && !_placements.Contains(placement))
            {
                _placements.Add(placement);
            }
        }
    }

    // Shift-click: into the group if it wasn't in it, out if it was.
    public void Toggle(SceneEntityPlacement placement)
    {
        if (placement == null)
        {
            return;
        }
        if (!_placements.Remove(placement))
        {
            _placements.Add(placement);
        }
    }

    // Midpoint of the selection — the gizmo's pivot, and what a rotate drag
    // orbits everything around.
    public Vector3 Pivot
    {
        get
        {
            if (_placements.Count == 0)
            {
                return Vector3.Zero;
            }
            Vector3 sum = Vector3.Zero;
            foreach (SceneEntityPlacement placement in _placements)
            {
                sum += placement.position;
            }
            return sum / _placements.Count;
        }
    }

    // Drops placements the scene no longer holds — after a delete, or an undo
    // of the placement that added one.
    public void Prune(SceneEntityDocument doc)
    {
        _placements.RemoveAll(p => !doc.Contains(p));
    }
}
