// Faction tag on a Mob. Targeting / aggro filters consume this so a friendly
// villager isn't perceived as a threat and a "confused" status effect can
// flip a hostile mob into briefly attacking its own team. Behaviors do not
// branch on team — the brain still drives "what to do next." Wire values
// are stable: append new entries, never reuse old numbers, so existing
// MobData.tres files keep loading after new teams are added.
public enum ETeam
{
	Hostile = 0,
	Friendly = 1,
	Neutral = 2,
	Player = 3,
}
