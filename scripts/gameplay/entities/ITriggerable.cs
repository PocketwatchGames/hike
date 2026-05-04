using Godot;

// Anything a trigger source can poke. Implementations live next to the
// behavior (SpikeDeployer pops spikes; a future DartShooter fires a dart;
// a PoisonCloudDeployer spawns a hazard area). The source argument is the
// firer — typically a TriggerSource (Area3D pressure plate), but also
// any other Node that wants to fire a trigger (a chest pinging deployers
// from its Complete handler, a mob's death pinging spawn points). Targets
// that need source-specific context (a SpikeDeployer reading
// TriggerSource.BodiesInArea to know who to damage) cast `source as
// TriggerSource`; targets that don't care just deploy.
public interface ITriggerable
{
    void Trigger(Node source);
}

// Optional sister interface for ITriggerables that should respond to a
// disarm signal from a host Trap. Splitting from ITriggerable lets pure
// "fire when poked" deployers (a one-shot poison cloud that's already
// over by the time you'd disarm it) skip the contract entirely.
public interface IDisarmable
{
    void Disarm();
}
