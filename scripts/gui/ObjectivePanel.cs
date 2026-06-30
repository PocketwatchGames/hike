using Godot;
using System;

public partial class ObjectivePanel : PanelContainer
{
	[Export] private ProgressBar _progressBar;
	[Export] private Label _titleLabel;
	[Export] private Label _countLabel;
	[Export] private Node _countContainer;
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}
}
