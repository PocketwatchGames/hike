// Whether a breakable entity is standing or lying in rubble, and when it comes
// back. A component rather than a base class: a breakable is a prop, a berry
// tree or whatever else, and keeps its own sim state — it just holds one of
// these (IBreakableSimState). The schedule is decided by the scene's
// Destructible at the moment of breaking and stored here as plain deadlines, so
// an unloaded breakable restores without its scene ever being instantiated.
public sealed class BreakState
{
    public bool Broken;

    // Rests still to sleep through before it stands again; 0 = not waiting on a
    // rest. Counted down by Sim.ResetSpawns.
    public int RestsLeft;

    // The WorldClockDays value it stands again at; +infinity = not waiting on
    // the clock. A whole number is a sunrise.
    public double RestoreAtClock = double.PositiveInfinity;

    public void MarkBroken(ERestoreSchedule schedule, int count, double nowClock)
    {
        Broken = true;
        RestsLeft = 0;
        RestoreAtClock = double.PositiveInfinity;
        int n = System.Math.Max(1, count);
        switch (schedule)
        {
            case ERestoreSchedule.Rests:
                RestsLeft = n;
                break;
            case ERestoreSchedule.Dawns:
                RestoreAtClock = System.Math.Floor(nowClock) + n;
                break;
        }
    }

    public void OnRest()
    {
        if (Broken && RestsLeft > 0 && --RestsLeft == 0)
        {
            Restore();
        }
    }

    // A clock deadline is applied when the entity next materializes, not when it
    // passes: rubble the player can see stays rubble until its chunk reloads, and
    // an unloaded one is indistinguishable either way.
    public void ResolveDeadline(double nowClock)
    {
        if (Broken && nowClock >= RestoreAtClock)
        {
            Restore();
        }
    }

    public void Restore()
    {
        Broken = false;
        RestsLeft = 0;
        RestoreAtClock = double.PositiveInfinity;
    }
}

public interface IBreakableSimState
{
    BreakState Break { get; }
}

// The node side: the entity root names its Destructible so the Sim can bind it
// to the state on spawn (see Sim.RegisterEntity).
public interface IBreakableEntity
{
    Destructible Destructible { get; }
}

// When a broken thing stands again. Authored on Destructible; never serialized
// (BreakState stores the resolved deadline), so it is free to grow.
public enum ERestoreSchedule
{
    // The Nth sleep to sunrise after breaking — 1 is "with the mobs".
    Rests,
    // The Nth sunrise after breaking, slept through or not.
    Dawns,
    // Stays broken. With no rubble authored it is removed from the world outright.
    Never,
}
