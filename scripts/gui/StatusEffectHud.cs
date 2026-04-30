using Godot;
using System;

[GlobalClass]
public partial class StatusEffectHud : MarginContainer
{
	[Export] TextureRect _icon;
	[Export] Label _count;
	[Export] ProgressBar _progressBar;
	[Export] Control _countContainer;
}
