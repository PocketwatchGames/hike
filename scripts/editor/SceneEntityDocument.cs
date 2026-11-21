using System;
using System.Collections.Generic;
using Godot;

// The scene editor's entities: the placements that ARE the scene, and the
// states each one has spawned into the editor's world as its live preview.
//
// One direction only. A placement spawns states; nothing ever reads a placement
// back off a state, so saving writes the placements as they are. Every tool
// edits placements and then calls Sync, which respawns exactly the ones whose
// spawn no longer matches — so a move, a retune and an undo all reach the
// preview the same way. The one reverse question, "which placement did this
// clicked state come from", is answered here for picking (OwnerOf) and nowhere
// else.
//
// Positions are in the editor's WORLD space while editing; the save converts
// them to scene-local.
public sealed class SceneEntityDocument
{
    private readonly WorldState _ws;
    private readonly List<SceneEntityPlacement> _placements = new();

    // SubsceneEntities.nextId — handed out at save to placements with no id.
    public int NextId = 1;

    // Fired after an undo or redo has rewritten the placements, so the editor
    // can Sync the preview to them.
    public Action Restored;

    private sealed class Spawn
    {
        public readonly List<EntitySimState> States = new();
        // The chunk each state was filed under. A live preview move (a gizmo
        // drag) changes a state's position without re-filing it, so the bucket
        // has to be remembered rather than re-derived.
        public readonly List<Vector3I> Buckets = new();
        public Vector3 Position;
        public float RotationY;
        public SpawnEntryData Entry;
        // The fork's fields as spawned. The property panel and undo both edit a
        // fork in place, which leaves Entry's reference unchanged.
        public Variant[] CustomValues;
    }

    private readonly Dictionary<SceneEntityPlacement, Spawn> _spawned = new();
    private readonly Dictionary<EntitySimState, SceneEntityPlacement> _owners = new();

    public SceneEntityDocument(WorldState ws)
    {
        _ws = ws;
    }

    public IReadOnlyList<SceneEntityPlacement> Placements => _placements;

    public bool Contains(SceneEntityPlacement placement) => placement != null && _placements.Contains(placement);

    public void Add(SceneEntityPlacement placement)
    {
        if (placement != null && !_placements.Contains(placement))
        {
            _placements.Add(placement);
        }
    }

    public void Remove(SceneEntityPlacement placement)
    {
        _placements.Remove(placement);
    }

    // Undo's write-back: the whole list, as it was.
    public void ReplaceAll(IEnumerable<SceneEntityPlacement> placements)
    {
        _placements.Clear();
        _placements.AddRange(placements);
    }

    // The placement a clicked state was spawned from, or null.
    public SceneEntityPlacement OwnerOf(EntitySimState state)
    {
        return state != null && _owners.TryGetValue(state, out SceneEntityPlacement p) ? p : null;
    }

    // What a placement currently has in the world; the first is its anchor.
    public IReadOnlyList<EntitySimState> StatesOf(SceneEntityPlacement placement)
    {
        return placement != null && _spawned.TryGetValue(placement, out Spawn spawn)
            ? spawn.States
            : Array.Empty<EntitySimState>();
    }

    // What one Sync changed in the world, for the editor's follow-up (free the
    // nodes, re-stamp the voxels entities own, respawn the chunks).
    public sealed class Changes
    {
        public readonly List<EntitySimState> Removed = new();
        public readonly List<EntitySimState> Added = new();
        public readonly HashSet<Vector3I> Chunks = new();
    }

    // Brings the world's states in line with the placements: despawns what is
    // no longer placed or no longer matches its placement, spawns what has no
    // states. A placement spawns on its own seed, so a respawn after a move
    // rolls what it rolled before.
    public Changes Sync(SpawnContext context)
    {
        var changes = new Changes();
        var placed = new HashSet<SceneEntityPlacement>(_placements);
        var stale = new List<SceneEntityPlacement>();
        foreach (KeyValuePair<SceneEntityPlacement, Spawn> kv in _spawned)
        {
            if (!placed.Contains(kv.Key) || !Matches(kv.Key, kv.Value))
            {
                stale.Add(kv.Key);
            }
        }
        foreach (SceneEntityPlacement p in stale)
        {
            Despawn(p, changes);
        }
        foreach (SceneEntityPlacement p in _placements)
        {
            if (!_spawned.ContainsKey(p))
            {
                SpawnPlacement(p, context, changes);
            }
        }
        return changes;
    }

    private static bool Matches(SceneEntityPlacement p, Spawn spawn)
    {
        return spawn.Position == p.position
            && Mathf.IsEqualApprox(spawn.RotationY, p.rotationY)
            && ReferenceEquals(spawn.Entry, p.Entry)
            && ResourceValues.Same(spawn.CustomValues, ResourceValues.Capture(p.custom));
    }

    private void Despawn(SceneEntityPlacement p, Changes changes)
    {
        Spawn spawn = _spawned[p];
        for (int i = 0; i < spawn.States.Count; i++)
        {
            EntitySimState state = spawn.States[i];
            _ws.GetEntities(spawn.Buckets[i])?.Remove(state);
            _owners.Remove(state);
            changes.Removed.Add(state);
            changes.Chunks.Add(spawn.Buckets[i]);
        }
        _spawned.Remove(p);
    }

    private void SpawnPlacement(SceneEntityPlacement p, SpawnContext context, Changes changes)
    {
        var spawn = new Spawn
        {
            Position = p.position,
            RotationY = p.rotationY,
            Entry = p.Entry,
            CustomValues = ResourceValues.Capture(p.custom),
        };
        _spawned[p] = spawn;
        if (p.Entry == null)
        {
            return;
        }
        float? outerFacing = context.FacingY;
        bool outerAuthored = context.AuthoredPosition;
        List<EntitySimState> outerRecording = _ws.RecordingAdds;
        _ws.RecordingAdds = spawn.States;
        try
        {
            // The author put it exactly here, so the gates that judge an
            // AUTO-PICKED spot are skipped — the same claim every stamp of the
            // scene makes for it.
            context.FacingY = p.rotationY;
            context.AuthoredPosition = true;
            if (!p.Entry.TrySpawn(_ws, p.position, new Random(p.seed), context))
            {
                GD.PushWarning($"Scene editor: {p.Label()} placed nothing at {p.position} — the entry "
                    + "refused the spot (see any error above it).");
            }
        }
        finally
        {
            _ws.RecordingAdds = outerRecording;
            context.FacingY = outerFacing;
            context.AuthoredPosition = outerAuthored;
        }
        foreach (EntitySimState state in spawn.States)
        {
            Vector3I bucket = Sim.WorldToChunkCoord(state.WorldPosition);
            spawn.Buckets.Add(bucket);
            _owners[state] = p;
            changes.Added.Add(state);
            changes.Chunks.Add(bucket);
        }
    }
}
