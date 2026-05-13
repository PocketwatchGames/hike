using Godot;
using Godot.Collections;

public partial class MobHUD : Node2D
{
	[Export] private TextureProgressBar _healthBar;
	[Export] private Array<TextureRect> _armorTextures;
	[Export] private TextureProgressBar _perceptionBar;
	[Export] private TextureProgressBar _discoveryBar;

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

		_discoveryBar.Visible = _mob.playerPerceptionState == EPlayerPerceptionState.Detected;
		_perceptionBar.Visible = _mob.mobData.team != ETeam.Neutral && _mob.mobData.team != ETeam.Friendly && _mob.mobData.team != ETeam.Player &&  _mob.perception > 0 && !_mob.triggered && _mob.playerCanSee;
		_healthBar.Visible = !_discoveryBar.Visible && (_mob.triggered || _perceptionBar.Visible) && !_mob.burrowed;
		if (!_perceptionBar.Visible && !_discoveryBar.Visible && !_healthBar.Visible)
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
		int activeArmorIndex = -1;
		if (_healthBar.Visible && _mob.maxArmor > 0f && _mob.armor > 0f)
		{
			float armorPercent = _mob.armor / _mob.maxArmor;
			if (armorPercent >= 1f)
			{
				activeArmorIndex = 4;
			}
			else
			{
				activeArmorIndex = Mathf.Clamp((int)Mathf.Floor(armorPercent * 4f), 0, 3);
			}
		}
		if (_armorTextures != null)
		{
			for (int i = 0; i < _armorTextures.Count; i++)
			{
				if (_armorTextures[i] != null)
				{
					_armorTextures[i].Visible = i == activeArmorIndex;
				}
			}
		}
		if (_perceptionBar != null)
		{
			_perceptionBar.Value = _mob.perception;
		}
		if (_discoveryBar != null)
		{
			_discoveryBar.Value = _mob.discoveryProgress;
		}
	}

	public override void _Process(double delta)
	{
		Update();
	}
}
