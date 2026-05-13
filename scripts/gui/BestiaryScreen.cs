using Godot;

[GlobalClass]
public partial class BestiaryScreen : Control
{
	GameClient _gameClient;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}
}
