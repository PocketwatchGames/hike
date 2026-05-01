using Godot;

// Distance-based footstep effect emitter for moving entities (Player, Mob).
// Mirrors WaterRippleEmitter: each holder ticks Update every physics frame
// while grounded, and a single one-shot effect is spawned each time the
// entity has moved more than `stride` meters in XZ since the last emit.
//
// Why distance-based rather than time-based: emission rate scales with speed
// without tracking velocity. A sneak emits fewer dust puffs than a sprint
// naturally; a stationary entity emits none. The host only needs a position,
// a "should emit" flag, the resolved EGroundType, and the per-host effect
// dictionary.
//
// EffectOneShot.Create parents the spawned node to the supplied `parent` and
// frees it once all child CpuParticles3D stop emitting. To keep the puff put
// in world space rather than tracking the actor, callers pass World (or any
// world-space root) as parent and GlobalPosition as the position.
public class FootstepEmitter
{
    private Vector2 _lastEmitXZ;
    private bool _hasLastEmit;

    public void Update(
        Node parent,
        Vector3 worldPos,
        bool emitting,
        float stride,
        EGroundType ground,
        Godot.Collections.Dictionary<EGroundType, PackedScene> effects)
    {
        if (!emitting)
        {
            _hasLastEmit = false;
            return;
        }
        Vector2 xz = new Vector2(worldPos.X, worldPos.Z);
        if (!_hasLastEmit)
        {
            _lastEmitXZ = xz;
            _hasLastEmit = true;
            return;
        }
        if (xz.DistanceSquaredTo(_lastEmitXZ) >= stride * stride)
        {
            Emit(parent, worldPos, ground, effects);
            _lastEmitXZ = xz;
        }
    }

    private static void Emit(
        Node parent,
        Vector3 worldPos,
        EGroundType ground,
        Godot.Collections.Dictionary<EGroundType, PackedScene> effects)
    {
        if (parent == null || effects == null)
        {
            return;
        }
        if (!effects.TryGetValue(ground, out PackedScene scene) || scene == null)
        {
            return;
        }
        EffectOneShot.Create(scene, parent, worldPos);
    }
}
