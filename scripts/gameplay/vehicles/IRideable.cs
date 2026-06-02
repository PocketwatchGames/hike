using Godot;

// Common contract for anything the player can ride — boats today, horses and
// other mounts later. The rideable owns its own movement physics in its
// _PhysicsProcess and reads the rider's steering intent through
// Player.MountMoveInput; the Player, while mounted, suspends its own
// locomotion (see Player.Mount) and parents itself under SeatAnchor so it
// travels with the vehicle. Boarding is initiated through the normal
// IInteractive flow — a "Board" verb whose Complete() calls Player.Mount —
// so vehicles also implement IInteractive (RideableVehicle does both).
public interface IRideable
{
    // Node the rider reparents under on mount. Its world transform places and
    // orients the rider; the rider keeps an identity local transform so it
    // faces the vehicle's forward.
    Node3D SeatAnchor { get; }

    // Looping animation slots the rider plays while seated. Resolved per-actor
    // through PlayerData.animations, so the rider's own art drives the pose
    // (boat paddle-rest vs paddle-stroke, saddle-idle vs gallop-seat).
    EAnimation IdleAnim { get; }
    EAnimation MoveAnim { get; }

    // True while the vehicle is actively being propelled by rider input —
    // drives the rider's idle-vs-move anim pick.
    bool IsPropelling { get; }

    // World position to drop the rider at on dismount (nearest shore / safe
    // ground beside the vehicle).
    Vector3 GetDismountPosition();

    // Lifecycle hooks fired by Player.Mount / Player.Dismount so the vehicle
    // can latch and release its rider reference and toggle its own state.
    void OnMounted(Player rider);
    void OnDismounted(Player rider);
}
