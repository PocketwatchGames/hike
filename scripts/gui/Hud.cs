using Godot;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

public partial class Hud : CanvasLayer
{
	[Export] public GameClient gameClient;
	[Export] PackedScene _statusEffectHudScene;
	[Export] WeaponHud _weaponLeftHud;
	[Export] WeaponHud _weaponRightHud;
	[Export] WeaponHud _consumableHud;
	[Export] ButtonHint _weaponLeftButtonHint;
	[Export] ButtonHint _weaponRightButtonHint;
	[Export] ButtonHint _consumableButtonHint;
	[Export] Control _statusEffectContainer;
	[Export] ProgressBar _healthBar;
	[Export] ProgressBar _armorBar;
	override public void _Ready()
	{
		gameClient.onInit += Init;
	}
	public void Init()
	{
	}

}
