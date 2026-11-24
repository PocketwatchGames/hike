using Godot;

// The player names the character they are controlling: a text box opens over
// the HUD and the conversation waits (ConversationController.Hold) until a name
// is confirmed, so the next line can follow from it. The name is the member's
// (PlayerState.characterName) and is saved with the party.
[GlobalClass]
public partial class NamePlayerAction : ConversationAction
{
	public override void Execute(ConversationContext ctx)
	{
		PlayerState member = ctx.player?.Member;
		Hud hud = Hud.Current;
		if (member == null || hud == null)
		{
			GD.PushError("NamePlayerAction: no controlled party member or no HUD to prompt on");
			return;
		}
		ConversationController controller = ctx.controller;
		controller?.Hold();
		hud.ShowNamePrompt(member.characterName, name =>
		{
			member.characterName = name;
			controller?.Release();
		});
	}
}
