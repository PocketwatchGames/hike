// One row's worth of player-progress data for a discovered mob species,
// stored in WorldSimState.DiscoveredMobs keyed by MobData. Holds the
// per-species kill count today; future bestiary fields (sightings, lore
// flags, drop log) live here so the dictionary stays one entry per
// species.
public class MobBestiaryEntry
{
	public int Kills;

	// Maps a kill count + cumulative thresholds to the bestiary level.
	// Empty / null thresholds = level 0 (the species doesn't level).
	// Otherwise level i+1 is reached once kills >= thresholds[i]; the
	// returned level is the highest such i+1, capped at thresholds.Count.
	// Shared by BestiaryScreen (for the displayed level + bar fill) and
	// GameClient (to detect level-up edges in OnMobKilled).
	public static int ComputeLevel(int kills, Godot.Collections.Array<int> killsPerLevel)
	{
		if (killsPerLevel == null || killsPerLevel.Count == 0)
		{
			return 0;
		}
		int level = 0;
		for (int i = 0; i < killsPerLevel.Count; i++)
		{
			if (kills >= killsPerLevel[i])
			{
				level = i + 1;
			}
			else
			{
				break;
			}
		}
		return level;
	}
}
