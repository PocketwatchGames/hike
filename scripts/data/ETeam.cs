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
	// Skittish wildlife (sparrows, kun-kuns). Not a combatant on any side —
	// flees/hides rather than fights, isn't counted as an ally by hostile
	// tactics, and its alarm calls draw a look (not an investigation) from
	// other factions while rallying only fellow prey.
	Prey = 4,
}

public static class Teams
{
	// True when two teams are on the same side for friendly-fire purposes —
	// neither should be able to land a non-friendly-fire hit on the other.
	// Same team is always allied; on top of that, Player and Friendly form a
	// single "player side" faction so the player can't hurt friendly NPCs or
	// a tamed companion (whose effective team is Friendly once tamed) with a
	// normal swing. Does NOT link any other pair — Hostile, Neutral, and Prey
	// only ally with their own kind.
	public static bool AreAllied(ETeam a, ETeam b)
	{
		if (a == b)
		{
			return true;
		}
		return IsPlayerSide(a) && IsPlayerSide(b);
	}

	private static bool IsPlayerSide(ETeam team)
	{
		return team == ETeam.Player || team == ETeam.Friendly;
	}
}
