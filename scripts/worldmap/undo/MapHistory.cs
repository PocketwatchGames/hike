using System.Collections.Generic;
using Godot;

// The painter's undo/redo stacks. Mirrors EditorHistory: open an edit on press,
// commit it on release, drop it if nothing moved.
public sealed class MapHistory
{
    public System.Action onChanged;

    private readonly WorldMapState _ctx;
    private readonly int _depth;
    private readonly List<MapEdit> _undo = new List<MapEdit>();
    private readonly List<MapEdit> _redo = new List<MapEdit>();
    private MapEdit _open;

    public MapHistory(WorldMapState ctx, int depth)
    {
        _ctx = ctx;
        _depth = Mathf.Max(1, depth);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsOpen => _open != null;

    // Safe to call on any press, including one that turns out to paint nothing:
    // an edit that captures no change is discarded at commit.
    public MapEdit Begin(string name)
    {
        _open ??= new MapEdit(name);
        return _open;
    }

    public void Commit()
    {
        if (_open == null)
        {
            return;
        }
        MapEdit edit = _open;
        _open = null;
        if (!edit.CaptureAfter(_ctx))
        {
            return;
        }
        _undo.Add(edit);
        if (_undo.Count > _depth)
        {
            _undo.RemoveAt(0);
        }
        // A new edit forks the timeline, so anything undone past this point is
        // no longer reachable.
        _redo.Clear();
        onChanged?.Invoke();
    }

    public MapEdit Undo()
    {
        if (_open != null || _undo.Count == 0)
        {
            return null;
        }
        MapEdit edit = _undo[_undo.Count - 1];
        _undo.RemoveAt(_undo.Count - 1);
        edit.Undo(_ctx);
        _redo.Add(edit);
        onChanged?.Invoke();
        return edit;
    }

    public MapEdit Redo()
    {
        if (_open != null || _redo.Count == 0)
        {
            return null;
        }
        MapEdit edit = _redo[_redo.Count - 1];
        _redo.RemoveAt(_redo.Count - 1);
        edit.Redo(_ctx);
        _undo.Add(edit);
        onChanged?.Invoke();
        return edit;
    }

    public void Clear()
    {
        _open = null;
        _undo.Clear();
        _redo.Clear();
        onChanged?.Invoke();
    }
}
