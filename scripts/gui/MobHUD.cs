using Godot;
using System;

public partial class MobHUD : Node2D
{
	[Export] private ProgressBar _healthBar;
	[Export] private ProgressBar _armorBar;
	[Export] private ProgressBar _aggroBar;
	[Export] private ProgressBar _perceptionBar;

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
		if (!_mob.alive || _mob.playerPerceptionState == EPlayerPerceptionState.Hidden)
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

		_aggroBar.Visible = _mob.perception > 0 && !_mob.triggered && _mob.playerCanSee;
		_perceptionBar.Visible = _mob.playerPerceptionState == EPlayerPerceptionState.Detected;
		_healthBar.Visible = (_mob.triggered || (_mob.playerCanSee && _mob.health < _mob.maxHealth)) && !_mob.burrowed;
		if (_armorBar != null)
		{
			_armorBar.Visible = _mob.maxArmor > 0f && _healthBar.Visible;
		}
		if (!_aggroBar.Visible && !_perceptionBar.Visible && !_healthBar.Visible)
		{
			Visible = false;
			return;
		}

		Visible = true;
		Position = GameClient.Current.ProjectToScreen(worldPosition);
		if (_healthBar != null)
		{
			_healthBar.MinValue = 0;
			_healthBar.MaxValue = _mob.maxHealth;
			_healthBar.Value = _mob.health;
		}
		if (_armorBar != null)
		{
			_armorBar.MinValue = 0;
			_armorBar.MaxValue = _mob.maxArmor > 0f ? _mob.maxArmor : 1f;
			_armorBar.Value = _mob.armor;
		}
		if (_aggroBar != null)
		{
			_aggroBar.Value = _mob.perception;
		}
		if (_perceptionBar != null)
		{
			_perceptionBar.Value = _mob.perceptionProgress;
		}
	}

	public override void _Process(double delta)
	{
		Update();
	}
}
