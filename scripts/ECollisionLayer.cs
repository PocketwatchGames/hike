using System;

[Flags]
public enum ECollisionLayer
{
    Environment = 1,
    Player = 2,
    Interactive = 4,
    Prop = 8,
    Mob = 16,
}
