using Godot;

// A mob's climb up onto — or controlled drop down off — a ledge taller than a
// stride.
//
// The split is by how the move READS, not by how far it is. A one-voxel riser
// is a stride: Mob.TryStepUp shoves a little vertical velocity at it, the body
// is over it inside a few ticks, and nobody sees anything. Two voxels is a
// CLIMB — the body has to leave the ground, clear a lip and land — and the same
// velocity shove there looks like the mob levitating up the wall. So a tall one
// runs as an authored action (MobData.mantleUpAction / mantleDownAction) for
// its animation, sound and duration, while this file carries the body along the
// arc, exactly as the player's mantle does and through the same LedgeCarry.
//
// The window is the mob's OWN traversal profile — maxStepHeight going up,
// maxFallHeight coming down — so a mantle only ever crosses ground its
// pathfinder had already decided it could cross. Nothing here widens where a
// mob can go; it changes how it gets there.
public partial class Mob
{
    // How far in front of the body to read the column being climbed. Must clear
    // the capsule or the probe reads the column the mob is already standing in.
    private const float MantleProbeReach = 0.7f;

    // Fallback carry duration when the authored action leaves durationSeconds
    // at 0. A traversal has to take SOME time — at zero the body teleports.
    private const float MantleFallbackDuration = 0.45f;

    // How far below the feet to look for the surface the mob is standing on
    // when measuring a rise. Generous enough to survive a tick spent slightly
    // off the floor mid-stride.
    private const float MantleFootingSearchDrop = 1.5f;

    // Deadlines on the SIM clock, like every other gameplay timer here: a
    // traversal must slow with slow-mo and stay frame-rate independent.
    private ulong _mantleStartMs;
    private ulong _mantleEndMs;
    private Vector3 _mantleFrom;
    private Vector3 _mantleTo;

    // While true the carry owns the body's position — steering, gravity and the
    // step-up assist all stand aside.
    public bool Mantling => _mantleEndMs != 0;

    // Begin a mantle if the column one step along `dir` is a ledge this mob can
    // take. Returns false for everything else, which is the common case, so the
    // caller falls through to the ordinary step-up lift.
    private bool TryStartMantle(Vector3 dir)
    {
        MobData data = _simState.MobData;
        if (data == null || Mantling || !alive || _swimming || _world == null)
        {
            return false;
        }
        // Mid-action the body belongs to the runner (a lunge owns its own
        // motion), and knockback owns it outright.
        if (_simState.KnockbackTime > 0f || _simState.MotionTime > 0f)
        {
            return false;
        }
        if (_runner != null && _runner.IsBusy)
        {
            return false;
        }
        if (data.mantleUpAction == null && data.mantleDownAction == null)
        {
            return false;
        }
        MobNavigator nav = Navigator;
        if (nav == null)
        {
            return false;
        }
        // Measure from the surface the mob is STANDING on, not its origin — a
        // body a few centimetres off the floor mid-stride would otherwise read
        // every rise as fractionally different.
        if (!TryFindFootingY(MantleFootingSearchDrop, out float footingY))
        {
            return false;
        }
        Vector3 ahead = GlobalPosition + dir * MantleProbeReach;
        if (!nav.TryGetSurfacePoint(ahead, footingY, out Vector3 landing))
        {
            return false;
        }
        // The shared rule decides what this crossing IS. A Walk is an ordinary
        // stride and the step-up assist owns it; a Fall or a Blocked is not
        // something to cross deliberately (and the ledge barrier is already
        // standing there refusing it). Only the Mantle band is ours.
        float rise = landing.Y - footingY;
        if (TraversalRule.Classify(nav.Profile, footingY, landing.Y) != EStepClass.Mantle)
        {
            return false;
        }
        InteractiveAction action = rise > 0f ? data.mantleUpAction : data.mantleDownAction;
        if (action == null)
        {
            return false;
        }

        float duration = action.durationSeconds > 0f ? action.durationSeconds : MantleFallbackDuration;
        _mantleFrom = GlobalPosition;
        _mantleTo = landing;
        _mantleStartMs = _world.GameTimeMs;
        _mantleEndMs = _mantleStartMs + (ulong)(duration * 1000f);

        // Face the ledge for the traversal, and set it outright rather than
        // easing: the steering that normally turns the body is suspended for the
        // duration, so there is nothing to ease it the rest of the way.
        Vector3 toLedge = _mantleTo - _mantleFrom;
        toLedge.Y = 0f;
        if (toLedge.LengthSquared() > 0.0001f)
        {
            Rotation = new Vector3(Rotation.X, Mathf.Atan2(toLedge.X, toLedge.Z), Rotation.Z);
        }

        // The action supplies the animation and any sound. It is started rather
        // than played directly so a mantle is authored the way every other mob
        // verb is, and so the runner reports the mob busy for its duration.
        _runner?.TryStart(action, new ActionContext());

        // Shares the player's mantle_debug switch — it is the same move, and the
        // two traces are most useful read side by side.
        if (CVars.mantleDebug.Value)
        {
            GD.Print($"[mantle] {_simState.Species?.ResourcePath ?? "mob"} "
                + $"{(rise > 0f ? "up" : "down")} rise={rise:F2} dur={duration:F2} "
                + $"from=({_mantleFrom.X:F2},{_mantleFrom.Y:F2},{_mantleFrom.Z:F2}) "
                + $"to=({_mantleTo.X:F2},{_mantleTo.Y:F2},{_mantleTo.Z:F2})");
        }
        return true;
    }

    // Carry the body through an in-flight mantle. Pinned for the duration so the
    // solver doesn't fight the written position, mirroring how a burrowed body
    // is held (see SetBurrowed).
    private void TickMantle()
    {
        if (!Mantling)
        {
            return;
        }
        // A hit mid-climb takes the body back. Knockback wins over a scripted
        // carry for the same reason it wins over an in-flight dart — being hit
        // has to move you.
        if (!alive || _simState.KnockbackTime > 0f)
        {
            EndMantle();
            return;
        }

        ulong now = _world.GameTimeMs;
        ulong span = _mantleEndMs - _mantleStartMs;
        float t = span == 0 ? 1f : Mathf.Clamp((now - _mantleStartMs) / (float)span, 0f, 1f);

        if (LinearVelocity != Vector3.Zero)
        {
            LinearVelocity = Vector3.Zero;
        }
        if (!Freeze)
        {
            Freeze = true;
        }
        GlobalPosition = LedgeCarry.Position(_mantleFrom, _mantleTo, t);

        if (t >= 1f)
        {
            EndMantle();
        }
    }

    // Hand the body back to physics. Unfrozen unconditionally: the settle-freeze
    // in _PhysicsProcess will re-pin it a tick later if it really is at rest,
    // and leaving it frozen in mid-climb would strand it.
    private void EndMantle()
    {
        _mantleStartMs = 0;
        _mantleEndMs = 0;
        if (Freeze)
        {
            Freeze = false;
        }
    }
}
