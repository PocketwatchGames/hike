using System;
using Godot;

// A box that fires actions and world-script hooks as actors move into and out
// of it (TriggerSpawnEntry). The runtime node, TriggerZone, only registers the
// box with Sim while its chunk is resident; Sim.TickTriggers does the testing.
public class TriggerSimState : EntitySimState
{
    // World-space box. WorldPosition is its centre on the floor, so the entity
    // streams with the chunk most of the box is in.
    public Aabb Box;
    public ETriggerActors Actors;
    public ETriggerRepeat Repeat;
    public StringName EnterSetsVariable;
    public StringName ExitSetsVariable;
    public ConversationAction[] EnterActions = Array.Empty<ConversationAction>();
    public ConversationAction[] ExitActions = Array.Empty<ConversationAction>();

    // Persisted so a Once trigger stays spent across a save.
    public bool EnterFired;
    public bool ExitFired;

    public TriggerSimState(Vector3 worldPosition, Aabb box)
        : base(worldPosition, scene: null)
    {
        Box = box;
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return TriggerZone.Create(sim, this);
    }

    // Nothing to walk around: a trigger has no body.
    public override void GetPathBlockerCells(Node3D entity, System.Collections.Generic.List<Vector3I> outCells) { }

    // Quarter turns keep the box axis-aligned: map two opposite corners and
    // take their bounds.
    public override void RotateQuarterTurns(int quarterTurns, Func<Vector3, Vector3> mapPosition)
    {
        base.RotateQuarterTurns(quarterTurns, mapPosition);
        Vector3 a = mapPosition(Box.Position);
        Vector3 b = mapPosition(Box.End);
        Box = new Aabb(a.Min(b), (a - b).Abs());
    }

    public bool Wants(ETriggerActors kind) => (Actors & kind) != 0;

    public bool WantsMobs => (Actors & (ETriggerActors.HostileMob | ETriggerActors.FriendlyMob
        | ETriggerActors.NeutralMob | ETriggerActors.PreyMob)) != 0;
}
