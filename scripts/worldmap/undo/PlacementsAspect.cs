using Godot;
using System;
using System.Collections.Generic;

// Everything in WorldMapPlacements: the subscene stamps (with their forks of
// their scenes' entities), the hand-placed entities, the trigger boxes, and the
// player spawn.
//
// Snapshotted WHOLE rather than tiled — these are a handful of entries, and
// every edit (add, delete, move, rotate) changes a list or one entry's fields,
// neither of which has a useful spatial extent.
//
// Restores by writing VALUES back into the existing instances rather than
// replacing them with copies. That keeps object identity, which is what lets a
// tool's selection survive an undo of the drag that moved it: undoing a move puts
// the anchor back on the object the tool is still holding, instead of leaving it
// pointing at an orphan.
//
// EVERY authored property is captured, found by asking the resource rather than
// from a list written here. The list is the thing that rots: this used to
// snapshot anchor / rotation / path and nothing else, so `yOffset` — which the
// scene tool's alt+click writes — sat outside undo entirely and a seat nudge
// could not be taken back. Every property added to a placement from here on
// would have joined it, silently, because nothing fails when a field is missing
// from a snapshot; it just stops being undoable.
public sealed class PlacementsAspect : IMapEditAspect
{
    // The values inside a placement's OWN copy (see EntityPlacement.EditableEntry).
    // A placement still using the shared palette file has nothing here: that
    // entry belongs to every other placement using it and is not ours to
    // restore, and the `custom` reference going back to null is itself captured
    // as an EntityPlacement property like any other.
    private static Variant[] CaptureEntry(EntityPlacement e)
    {
        return ResourceValues.Capture(e?.custom);
    }

    private readonly struct Snapshot
    {
        public readonly SubscenePlacement[] Stamps;
        public readonly Variant[][] StampValues;
        // Per stamp, its overrides and the fields of each one's fork. The
        // override LIST is a stamp property, captured above as a reference; these
        // are what the panel edits inside it.
        public readonly SubsceneEntityOverride[][] Overrides;
        public readonly Variant[][][] OverrideValues;
        public readonly Variant[][][] OverrideEntryValues;

        public readonly EntityPlacement[] Entities;
        public readonly Variant[][] EntityValues;
        public readonly Variant[][] EntryValues;

        // Trigger boxes are entity placements too, in their own list; their
        // whole entry is the placement's own (`custom`).
        public readonly EntityPlacement[] Triggers;
        public readonly Variant[][] TriggerValues;
        public readonly Variant[][] TriggerEntryValues;

        public readonly bool HasSpawn;
        public readonly Vector2I SpawnXZ;

        public Snapshot(WorldMapPlacements p)
        {
            Stamps = (SubscenePlacement[])p.placements.Clone();
            StampValues = new Variant[Stamps.Length][];
            Overrides = new SubsceneEntityOverride[Stamps.Length][];
            OverrideValues = new Variant[Stamps.Length][][];
            OverrideEntryValues = new Variant[Stamps.Length][][];
            for (int i = 0; i < Stamps.Length; i++)
            {
                StampValues[i] = ResourceValues.Capture(Stamps[i]);
                SubsceneEntityOverride[] list = (SubsceneEntityOverride[])Stamps[i]?.entityOverrides?.Clone()
                    ?? System.Array.Empty<SubsceneEntityOverride>();
                Overrides[i] = list;
                OverrideValues[i] = new Variant[list.Length][];
                OverrideEntryValues[i] = new Variant[list.Length][];
                for (int k = 0; k < list.Length; k++)
                {
                    OverrideValues[i][k] = ResourceValues.Capture(list[k]);
                    OverrideEntryValues[i][k] = ResourceValues.Capture(list[k]?.custom);
                }
            }

            Entities = (EntityPlacement[])p.entities.Clone();
            EntityValues = new Variant[Entities.Length][];
            EntryValues = new Variant[Entities.Length][];
            for (int i = 0; i < Entities.Length; i++)
            {
                EntityValues[i] = ResourceValues.Capture(Entities[i]);
                EntryValues[i] = CaptureEntry(Entities[i]);
            }

            Triggers = (EntityPlacement[])p.triggers.Clone();
            TriggerValues = new Variant[Triggers.Length][];
            TriggerEntryValues = new Variant[Triggers.Length][];
            for (int i = 0; i < Triggers.Length; i++)
            {
                TriggerValues[i] = ResourceValues.Capture(Triggers[i]);
                TriggerEntryValues[i] = CaptureEntry(Triggers[i]);
            }

            HasSpawn = p.hasSpawn;
            SpawnXZ = p.spawnXZ;
        }

        public bool Matches(Snapshot other)
        {
            if (Stamps.Length != other.Stamps.Length
                || Entities.Length != other.Entities.Length
                || Triggers.Length != other.Triggers.Length
                || HasSpawn != other.HasSpawn
                || SpawnXZ != other.SpawnXZ)
            {
                return false;
            }
            for (int i = 0; i < Stamps.Length; i++)
            {
                if (!ReferenceEquals(Stamps[i], other.Stamps[i])
                    || !ResourceValues.Same(StampValues[i], other.StampValues[i])
                    || Overrides[i].Length != other.Overrides[i].Length)
                {
                    return false;
                }
                for (int k = 0; k < Overrides[i].Length; k++)
                {
                    if (!ReferenceEquals(Overrides[i][k], other.Overrides[i][k])
                        || !ResourceValues.Same(OverrideValues[i][k], other.OverrideValues[i][k])
                        || !ResourceValues.Same(OverrideEntryValues[i][k], other.OverrideEntryValues[i][k]))
                    {
                        return false;
                    }
                }
            }
            for (int i = 0; i < Entities.Length; i++)
            {
                if (!ReferenceEquals(Entities[i], other.Entities[i])
                    || !ResourceValues.Same(EntityValues[i], other.EntityValues[i])
                    || !ResourceValues.Same(EntryValues[i], other.EntryValues[i]))
                {
                    return false;
                }
            }
            for (int i = 0; i < Triggers.Length; i++)
            {
                if (!ReferenceEquals(Triggers[i], other.Triggers[i])
                    || !ResourceValues.Same(TriggerValues[i], other.TriggerValues[i])
                    || !ResourceValues.Same(TriggerEntryValues[i], other.TriggerEntryValues[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public void Apply(WorldMapState ctx)
        {
            ctx.Placements.placements = (SubscenePlacement[])Stamps.Clone();
            for (int i = 0; i < Stamps.Length; i++)
            {
                // The stamp first: it carries the override list, and the values
                // below land in the overrides that list holds again.
                ResourceValues.Write(Stamps[i], StampValues[i]);
                for (int k = 0; k < Overrides[i].Length; k++)
                {
                    ResourceValues.Write(Overrides[i][k], OverrideValues[i][k]);
                    ResourceValues.Write(Overrides[i][k]?.custom, OverrideEntryValues[i][k]);
                }
            }

            ctx.Placements.entities = (EntityPlacement[])Entities.Clone();
            for (int i = 0; i < Entities.Length; i++)
            {
                // Placement first: it carries the `custom` reference, so the
                // values below land in the copy this placement is holding again
                // — which for the undo of a first edit is null (nothing to
                // write), and for its redo is the fork.
                ResourceValues.Write(Entities[i], EntityValues[i]);
                ResourceValues.Write(Entities[i]?.custom, EntryValues[i]);
            }

            ctx.Placements.triggers = (EntityPlacement[])Triggers.Clone();
            for (int i = 0; i < Triggers.Length; i++)
            {
                ResourceValues.Write(Triggers[i], TriggerValues[i]);
                ResourceValues.Write(Triggers[i]?.custom, TriggerEntryValues[i]);
            }

            ctx.Placements.hasSpawn = HasSpawn;
            ctx.Placements.spawnXZ = SpawnXZ;
        }
    }

    private bool _touched;
    private Snapshot _before;
    private Snapshot _after;

    public void Touch(WorldMapState ctx)
    {
        if (_touched)
        {
            return;
        }
        _touched = true;
        _before = new Snapshot(ctx.Placements);
    }

    public bool CaptureAfter(WorldMapState ctx)
    {
        if (!_touched)
        {
            return false;
        }
        _after = new Snapshot(ctx.Placements);
        return !_after.Matches(_before);
    }

    public void Restore(WorldMapState ctx, bool redo)
    {
        if (redo)
        {
            _after.Apply(ctx);
        }
        else
        {
            _before.Apply(ctx);
        }
    }
}
