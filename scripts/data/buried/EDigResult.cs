// Outcome class of a shovel dig, used to pick the completion effect. Ordered by
// escalation: an empty hole, a common find (carrot / loot / surprise critter),
// or treasure. The shovel's Dig event authors one effect per class.
public enum EDigResult
{
    Nothing,
    Common,
    Treasure,
}
