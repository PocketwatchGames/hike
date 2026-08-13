using System;
using System.Collections.Generic;

// Undo / redo for the world editor. A tool opens an edit, declares what it is
// about to change on it, writes, then commits:
//
//     EditorEdit edit = history.Begin("Paint Wall");
//     edit.TouchVoxel(cell);              // BEFORE the write, always
//     worldState.SetBlockWorld(...);
//     history.Commit();                   // dropped if nothing actually changed
//
// A whole drag is one edit: Begin on press, Commit on release. Nothing here
// knows what a brush or a stamp is, so adding a tool needs no changes to this
// file — and adding a new KIND of undoable state means one new
// IEditorEditAspect, not a case in a switch.
public sealed class EditorHistory
{
    // Fired whenever the stacks change, for HUD availability / dirty markers.
    public Action onChanged;

    private readonly WorldState _world;
    private readonly Sim _sim;
    private readonly int _depth;
    private readonly List<EditorEdit> _undo = new List<EditorEdit>();
    private readonly List<EditorEdit> _redo = new List<EditorEdit>();
    private EditorEdit _open;

    public EditorHistory(WorldState world, Sim sim, int depth)
    {
        _world = world;
        _sim = sim;
        _depth = Math.Max(1, depth);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // The edit currently open, or null between Commit and the next Begin.
    public EditorEdit Current => _open;

    public EditorEdit Begin(string name)
    {
        // A tool that forgot to commit must not swallow the next action.
        Commit();
        _open = new EditorEdit(name, _world);
        return _open;
    }

    public void Commit()
    {
        EditorEdit edit = _open;
        _open = null;
        if (edit == null || !edit.CaptureAfter())
        {
            return;
        }
        _undo.Add(edit);
        // Anything redoable is a branch the author just abandoned.
        _redo.Clear();
        while (_undo.Count > _depth)
        {
            _undo.RemoveAt(0);
        }
        onChanged?.Invoke();
    }

    // Both return the edit that moved, or null when the stack was empty.
    public EditorEdit Undo()
    {
        return Step(_undo, _redo, redo: false);
    }

    public EditorEdit Redo()
    {
        return Step(_redo, _undo, redo: true);
    }

    public void Clear()
    {
        _open = null;
        _undo.Clear();
        _redo.Clear();
        onChanged?.Invoke();
    }

    private EditorEdit Step(List<EditorEdit> from, List<EditorEdit> to, bool redo)
    {
        // Fold an in-flight stroke in first, so undo during a drag reverses the
        // stroke so far rather than the action before it.
        Commit();
        if (from.Count == 0)
        {
            return null;
        }
        EditorEdit edit = from[from.Count - 1];
        from.RemoveAt(from.Count - 1);

        var refresh = new EditorRefresh();
        if (redo)
        {
            edit.Redo(refresh);
        }
        else
        {
            edit.Undo(refresh);
        }
        refresh.Apply(_sim);

        to.Add(edit);
        onChanged?.Invoke();
        return edit;
    }
}
