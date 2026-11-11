using Godot;

// One entity a stamped scene places, as the painter selects and edits it: which
// stamp, and which of its scene's entities by id. Not movable and not deletable
// — where it stands is the scene's — but its entry can be forked for THIS stamp
// (SubscenePlacement.entityOverrides), which is how one building's front door
// gets its own lock.
//
// Held by (stamp, id) rather than by the SceneEntity, which is a per-rotation
// cache object: turning the stamp swaps every SceneEntity, and a selection must
// survive that. WorldMapState hands out one instance per pair, so the panel and
// the map can compare by reference.
public sealed class SceneEntityTarget : IEntryEditTarget
{
    private readonly WorldMapState _map;
    public readonly SubscenePlacement Stamp;
    public readonly int Id;

    public SceneEntityTarget(WorldMapState map, SubscenePlacement stamp, int id)
    {
        _map = map;
        Stamp = stamp;
        Id = id;
    }

    // The entity as the stamp currently places it, or null once the stamp has
    // been deleted or its scene no longer has this id.
    public SceneEntity Entity
    {
        get
        {
            if (System.Array.IndexOf(_map.Placements.placements, Stamp) < 0)
            {
                return null;
            }
            SubsceneState sub = _map.SubsceneFor(Stamp);
            if (sub == null)
            {
                return null;
            }
            foreach (SceneEntity e in sub.Entities)
            {
                if (e.Id == Id)
                {
                    return e;
                }
            }
            return null;
        }
    }

    public bool IsOverridden => Stamp.OverrideFor(Id)?.custom != null;

    public SpawnEntryData Entry => Entity?.EntryFor(Stamp);

    // The stamp's own copy, made on the first edit. The override list is
    // REPLACED rather than appended to in place, so the painter's undo — which
    // captures the stamp's fields — sees the change.
    public SpawnEntryData EditableEntry()
    {
        SceneEntity entity = Entity;
        if (entity == null)
        {
            return null;
        }
        SubsceneEntityOverride existing = Stamp.OverrideFor(Id);
        if (existing?.custom != null && !entity.HasMismatchedOverride(Stamp))
        {
            return existing.custom;
        }
        SpawnEntryData authored = entity.Placement.Entry;
        if (authored == null)
        {
            return null;
        }
        SpawnEntryData fork = authored.Fork();
        if (ReferenceEquals(fork, authored))
        {
            return authored;
        }
        if (existing != null)
        {
            existing.custom = fork;
            return fork;
        }
        var list = new System.Collections.Generic.List<SubsceneEntityOverride>(
            Stamp.entityOverrides ?? System.Array.Empty<SubsceneEntityOverride>())
        {
            new SubsceneEntityOverride { entityId = Id, custom = fork },
        };
        Stamp.entityOverrides = list.ToArray();
        return fork;
    }

    public string DisplayName()
    {
        SceneEntity entity = Entity;
        string what = entity == null ? $"entity {Id}" : entity.Placement.Label();
        string mark = IsOverridden ? " — this stamp's own" : "";
        return $"{Stamp.path.GetFile().GetBaseName()} › {what}{mark}";
    }
}
