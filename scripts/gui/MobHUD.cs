using Godot;
using System;

public partial class MobHUD : Node2D
{
	[Export] private ProgressBar _healthBar;
	[Export] private ProgressBar _aggroBar;

	Camera3D _camera;
	Mob _mob;

	public static void Create(PackedScene scene, Camera3D camera, Mob mob, Node parent)
	{
		var hud = scene.Instantiate<MobHUD>();
		hud.Init(camera, mob, parent);
	}

	void Init(Camera3D camera, Mob mob, Node parent)
	{
		_camera = camera;
		_mob = mob;
		if (parent != null)
		{
			parent.AddChild(this);
		}
		_mob.TreeExiting += QueueFree;
		Update();
	}

	void Update()
	{
		if (!_mob.alive || (_mob.health >= _mob.maxHealth && _mob.aggro <= 0f))
		{
			Visible = false;
			return;
		}
		Vector3 worldPosition = _mob.HudAnchor != null ? _mob.HudAnchor.GlobalPosition : _mob.GlobalPosition;
		if (_camera.IsPositionBehind(worldPosition))
		{
			Visible = false;
			return;
		}

		_aggroBar.Visible = _mob.aggro > 0 && _mob.aggro < 1;
		_healthBar.Visible = _mob.aggro >= 1 || (_mob.aggro <= 0 && _mob.health < _mob.maxHealth);
		Visible = true;
		Position = _camera.UnprojectPosition(worldPosition);
		if (_healthBar != null)
		{
			_healthBar.Value = _mob.health;
		}
		if (_aggroBar != null)
		{
			_aggroBar.Value = _mob.aggro;
		}
	}

	public override void _Process(double delta)
	{
		Update();
	}
}
