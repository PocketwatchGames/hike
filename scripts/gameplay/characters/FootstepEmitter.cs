using Godot;

// One-shot footstep FX dispatch for Player / Mob. Stateless — callers
// decide when to fire (an animation frame match drives the timing).
//
// Fx.Create parents the spawned node to the supplied `parent` and frees
// it once all child CpuParticles3D stop emitting. To keep the puff put in
// world space rather than tracking the actor, callers pass Sim (or any
// world-space root) as parent and GlobalPosition as the position.
public static class FootstepEmitter
{
    public static void Emit(
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
        Fx.Create(scene, parent, worldPos);
    }

    public static void Emit(Node parent, Vector3 worldPos, PackedScene effect)
    {
        if (parent == null || effect == null)
        {
            return;
        }
        Fx.Create(effect, parent, worldPos);
    }
}
