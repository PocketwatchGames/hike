using System;

// Who reacts to a discrete Sim.CreateNoiseEvent. A noise can alert other mobs
// (raising their perception of the source — weapon impacts, breaking objects),
// the player (raising their awareness of the source mob — a barking dog), or
// both. Flags so a single event targets any combination; default is Mobs.
[Flags]
public enum ENoiseAudience
{
    None = 0,
    Mobs = 1,
    Player = 2,
    All = Mobs | Player,
}
