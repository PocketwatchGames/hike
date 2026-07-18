using Godot;

// "Rescue XXX!" — spawned in code when a party member dies (GameClient
// .OnPlayerDiedInternal), tracking that fallen member's corpse. Completes when
// the member is revived, fails when the corpse is destroyed. The textKey should
// carry a "%0" placeholder for the fallen member's name.
[GlobalClass]
public partial class RescueQuestData : QuestData
{
    public override QuestState CreateRuntime() => new RescueQuest(this);
}
