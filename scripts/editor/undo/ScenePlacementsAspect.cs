using System.Collections.Generic;
using Godot;

// The scene's entity placements (SceneEntityDocument): which there are, and
// where each stands, which way it faces and which entry it holds. The states in
// the world are only their preview, so undo restores the placements and lets
// the document re-sync the preview — nothing here touches a state.
//
// Restores by writing values back into the existing instances, so a selection
// (which holds placements) survives the undo of the drag that moved it.
public sealed class ScenePlacementsAspect : IEditorEditAspect
{
    private readonly struct Values
    {
        public readonly Vector3 Position;
        public readonly float RotationY;
        public readonly SpawnEntryData Custom;
        // The fork's own fields: the property panel edits a fork IN PLACE after
        // the first edit made it, so the reference alone would miss every edit
        // but the first.
        public readonly Variant[] CustomValues;

        public Values(SceneEntityPlacement p)
        {
            Position = p.position;
            RotationY = p.rotationY;
            Custom = p.custom;
            CustomValues = ResourceValues.Capture(p.custom);
        }

        public bool Same(Values other)
        {
            return Position == other.Position
                && Mathf.IsEqualApprox(RotationY, other.RotationY)
                && ReferenceEquals(Custom, other.Custom)
                && ResourceValues.Same(CustomValues, other.CustomValues);
        }

        public void ApplyTo(SceneEntityPlacement p)
        {
            p.position = Position;
            p.rotationY = RotationY;
            // The reference first, so the values land in the copy this
            // placement holds again — none for the undo of a first edit.
            p.custom = Custom;
            ResourceValues.Write(p.custom, CustomValues);
        }
    }

    private readonly struct Snapshot
    {
        public readonly SceneEntityPlacement[] Placements;
        public readonly Values[] Fields;

        public Snapshot(SceneEntityDocument doc)
        {
            Placements = new SceneEntityPlacement[doc.Placements.Count];
            Fields = new Values[Placements.Length];
            for (int i = 0; i < Placements.Length; i++)
            {
                Placements[i] = doc.Placements[i];
                Fields[i] = new Values(Placements[i]);
            }
        }

        public bool Matches(Snapshot other)
        {
            if (Placements.Length != other.Placements.Length)
            {
                return false;
            }
            for (int i = 0; i < Placements.Length; i++)
            {
                if (!ReferenceEquals(Placements[i], other.Placements[i]) || !Fields[i].Same(other.Fields[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public void Apply(SceneEntityDocument doc)
        {
            for (int i = 0; i < Placements.Length; i++)
            {
                Fields[i].ApplyTo(Placements[i]);
            }
            doc.ReplaceAll(Placements);
        }
    }

    private readonly SceneEntityDocument _doc;
    private Snapshot _before;
    private Snapshot _after;
    private bool _touched;

    public ScenePlacementsAspect(SceneEntityDocument doc)
    {
        _doc = doc;
    }

    public void Touch()
    {
        if (_touched)
        {
            return;
        }
        _touched = true;
        _before = new Snapshot(_doc);
    }

    public bool CaptureAfter(WorldState world)
    {
        if (!_touched)
        {
            return false;
        }
        _after = new Snapshot(_doc);
        return !_after.Matches(_before);
    }

    public void Restore(WorldState world, bool redo, EditorRefresh refresh)
    {
        (redo ? _after : _before).Apply(_doc);
        _doc.Restored?.Invoke();
    }
}
