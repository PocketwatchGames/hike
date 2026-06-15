using Godot;

// Anything a trigger source can poke. The source argument is the firer —
// typically a TriggerSource (Area3D pressure plate), but also any other Node
// (a chest pinging deployers, a mob's death pinging spawn points). Targets
// that need source-specific context (a SpikeDeployer reading
// TriggerSource.BodiesInArea to know who to damage) cast `source as
// TriggerSource`; targets that don't care just deploy.
public interface ITriggerable
{
    void Trigger(Node source);
}

// Sister interface for ITriggerables that respond to a disarm signal from a
// host Trap. Split from ITriggerable so pure "fire when poked" deployers can
// skip the contract.
public interface IDisarmable
{
    void Disarm();
}
