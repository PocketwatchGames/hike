using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class Hud : CanvasLayer
{
	[Export] public GameClient gameClient;
	override public void _Ready()
	{
		gameClient.onInit += Init;
	}
	public void Init()
	{
	}

}
