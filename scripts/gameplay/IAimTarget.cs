using Godot;

// Implemented by anything an attack can be aimed AT — the player and mobs.
// Ranged mob attacks read AimCenter to fire a true 3D heading at the target's
// body center instead of along the attacker's flat yaw. This is the target side
// of aiming, distinct from IActionActor's attacker side (the player's own aim
// rides ActorForward, which already folds in auto-aim pitch).
public interface IAimTarget
{
    // World-space body center to aim at — the hurtbox shape's center, well above
    // the feet, so a shot from above or below leads correctly in pitch.
    Vector3 AimCenter { get; }
}
