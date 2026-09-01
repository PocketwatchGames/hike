using Godot;
using System;
using System.Collections.Generic;

// Everything in WorldMapPlacements: the subscene stamps, the hand-placed
// entities, and the player spawn.
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
    // The script-declared properties of a placement resource, cached per type.
    // ScriptVariable is the flag Godot sets on a script's own exports, so engine
    // bookkeeping (resource_path, script, resource_local_to_scene) is skipped —
    // restoring THOSE would do real damage.
    private static readonly Dictionary<Type, StringName[]> _propsByType = new();

    private static StringName[] PropsOf(Resource r)
    {
        Type type = r.GetType();
        if (_propsByType.TryGetValue(type, out StringName[] cached))
        {
            return cached;
        }
        var names = new List<StringName>();
        foreach (Godot.Collections.Dictionary entry in r.GetPropertyList())
        {
            var usage = (PropertyUsageFlags)(long)entry["usage"];
            if ((usage & PropertyUsageFlags.ScriptVariable) != 0)
            {
                names.Add(new StringName(entry["name"].AsString()));
            }
        }
        cached = names.ToArray();
        _propsByType[type] = cached;
        return cached;
    }

    private static Variant[] Capture(Resource r)
    {
        if (r == null)
        {
            return null;
        }
        StringName[] props = PropsOf(r);
        var values = new Variant[props.Length];
        for (int i = 0; i < props.Length; i++)
        {
            values[i] = r.Get(props[i]);
        }
        return values;
    }

    // The values inside a placement's OWN copy (see EntityPlacement.EditableEntry).
    // A placement still using the shared palette file has nothing here: that
    // entry belongs to every other placement using it and is not ours to
    // restore, and the `custom` reference going back to null is itself captured
    // as an EntityPlacement property like any other.
    private static Variant[] CaptureEntry(EntityPlacement e)
    {
        return Capture(e?.custom);
    }

    private static void Write(Resource r, Variant[] values)
    {
        if (r == null || values == null)
        {
            return;
        }
        StringName[] props = PropsOf(r);
        for (int i = 0; i < props.Length && i < values.Length; i++)
        {
            r.Set(props[i], values[i]);
        }
    }

    private static bool SameValues(Variant[] a, Variant[] b)
    {
        if (a == null || b == null)
        {
            return ReferenceEquals(a, b);
        }
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            // Compared as TEXT, deliberately. Variant does not compare by value
            // here — MEASURED: two Variants holding the same Vector2I are not
            // Equal, so a no-op press registered as a change and cost an undo
            // slot every time. Every property a placement can hold prints
            // faithfully (ints, strings, enums, Vector2I, bools) and an object
            // reference prints its instance id, so identity still compares as
            // identity.
            if (a[i].ToString() != b[i].ToString())
            {
                return false;
            }
        }
        return true;
    }

    private readonly struct Snapshot
    {
        public readonly SubscenePlacement[] Stamps;
        public readonly Variant[][] StampValues;

        public readonly EntityPlacement[] Entities;
        public readonly Variant[][] EntityValues;
        public readonly Variant[][] EntryValues;

        public readonly bool HasSpawn;
        public readonly Vector2I SpawnXZ;

        public Snapshot(WorldMapPlacements p)
        {
            Stamps = (SubscenePlacement[])p.placements.Clone();
            StampValues = new Variant[Stamps.Length][];
            for (int i = 0; i < Stamps.Length; i++)
            {
                StampValues[i] = Capture(Stamps[i]);
            }

            Entities = (EntityPlacement[])p.entities.Clone();
            EntityValues = new Variant[Entities.Length][];
            EntryValues = new Variant[Entities.Length][];
            for (int i = 0; i < Entities.Length; i++)
            {
                EntityValues[i] = Capture(Entities[i]);
                EntryValues[i] = CaptureEntry(Entities[i]);
            }

            HasSpawn = p.hasSpawn;
            SpawnXZ = p.spawnXZ;
        }

        public bool Matches(Snapshot other)
        {
            if (Stamps.Length != other.Stamps.Length
                || Entities.Length != other.Entities.Length
                || HasSpawn != other.HasSpawn
                || SpawnXZ != other.SpawnXZ)
            {
                return false;
            }
            for (int i = 0; i < Stamps.Length; i++)
            {
                if (!ReferenceEquals(Stamps[i], other.Stamps[i])
                    || !SameValues(StampValues[i], other.StampValues[i]))
                {
                    return false;
                }
            }
            for (int i = 0; i < Entities.Length; i++)
            {
                if (!ReferenceEquals(Entities[i], other.Entities[i])
                    || !SameValues(EntityValues[i], other.EntityValues[i])
                    || !SameValues(EntryValues[i], other.EntryValues[i]))
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
                Write(Stamps[i], StampValues[i]);
            }

            ctx.Placements.entities = (EntityPlacement[])Entities.Clone();
            for (int i = 0; i < Entities.Length; i++)
            {
                // Placement first: it carries the `custom` reference, so the
                // values below land in the copy this placement is holding again
                // — which for the undo of a first edit is null (nothing to
                // write), and for its redo is the fork.
                Write(Entities[i], EntityValues[i]);
                Write(Entities[i]?.custom, EntryValues[i]);
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
