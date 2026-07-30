using System.Collections.Generic;
using Godot;

// Position + facing of individual entities, for tools that move or rotate what
// is already placed.
//
// EntityBucketsAspect can't cover this: it snapshots each chunk's list but holds
// the EntitySimStates by reference, so an in-place move mutates its "before"
// snapshot too and the edit reads as a no-op. This one snapshots the values.
// A move that crosses a chunk boundary also changes bucket membership, so the
// tool touches both aspects and they compose — this restores the transform, the
// bucket aspect restores which chunk the entity is filed under.
public sealed class EntityTransformsAspect : IEditorEditAspect
{
    private readonly struct EntityTransform
    {
        public readonly Vector3 Position;
        public readonly float RotationY;

        public EntityTransform(EntitySimState state)
        {
            Position = state.WorldPosition;
            RotationY = state.RotationY;
        }

        public bool Matches(EntitySimState state)
        {
            return state.WorldPosition == Position && state.RotationY == RotationY;
        }

        public void ApplyTo(EntitySimState state)
        {
            state.WorldPosition = Position;
            state.RotationY = RotationY;
        }
    }

    private Dictionary<EntitySimState, EntityTransform> _before = new Dictionary<EntitySimState, EntityTransform>();
    private readonly Dictionary<EntitySimState, EntityTransform> _after = new Dictionary<EntitySimState, EntityTransform>();

    public void Touch(EntitySimState state)
    {
        if (state != null && !_before.ContainsKey(state))
        {
            _before[state] = new EntityTransform(state);
        }
    }

    public bool CaptureAfter(WorldState world)
    {
        var changed = new Dictionary<EntitySimState, EntityTransform>(_before.Count);
        foreach (KeyValuePair<EntitySimState, EntityTransform> kvp in _before)
        {
            if (kvp.Value.Matches(kvp.Key))
            {
                continue;
            }
            changed[kvp.Key] = kvp.Value;
            _after[kvp.Key] = new EntityTransform(kvp.Key);
        }
        _before = changed;
        return _after.Count > 0;
    }

    public void Restore(WorldState world, bool redo, EditorRefresh refresh)
    {
        Dictionary<EntitySimState, EntityTransform> target = redo ? _after : _before;
        foreach (KeyValuePair<EntitySimState, EntityTransform> kvp in target)
        {
            // Both ends of the move: the chunk the entity is leaving needs its
            // nodes respawned just as much as the one it lands in.
            refresh.AddEntityChunk(Sim.WorldToChunkCoord(kvp.Key.WorldPosition));
            kvp.Value.ApplyTo(kvp.Key);
            refresh.AddEntityChunk(Sim.WorldToChunkCoord(kvp.Key.WorldPosition));
        }
    }
}
