using Godot;

// "Kunkun Hunt (X/12)" — count player-credited kills of a target species up to
// a goal. Progress is a Counter. Identity is the SpeciesData reference itself
// (species have no separate string id).
[GlobalClass]
public partial class KillCountQuestData : QuestData
{
    // The species whose kills count toward this quest.
    [Export] public SpeciesData targetSpecies;

    // Kills required to complete.
    [Export] public int targetCount = 12;

    public override QuestState CreateRuntime() => new KillCountQuest(this);
}
