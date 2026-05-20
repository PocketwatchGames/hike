using Godot;
using Godot.Collections;

public partial class MobHUD : Node2D
{
	const float PerceptionScale = 0.5f;
	const float PerceptionAlpha = 0.5f;
	const float DiscoveredAlpha = 0.5f;
	const float TriggeredAlpha = 0.75f;
	const float HideThreshold = 0.01f;
	const float AnimSpeed = 12f;

	[Export] private TextureProgressBar _healthBar;
	[Export] private TextureProgressBar _armorBar;
	[Export] private TextureProgressBar _perceptionBar;
	[Export] private TextureProgressBar _discoveryBar;
	[Export] private Label _debugLabel;

	Camera3D _camera;
	Mob _mob;
	float _curScale;
	float _curAlpha;

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
		// The debug label has to render independent of the perception-bar
		// fade animation (scale + modulate cascade from this node to its
		// children). Reparent it to MobHUD's parent so it becomes a sibling;
		// position is driven manually in _Process from the projected mob
		// position, and lifetime piggybacks on _ExitTree below.
		if (_debugLabel != null && parent != null)
		{
			_debugLabel.Reparent(parent);
		}
		_mob.TreeExiting += QueueFree;
		_curScale = 0f;
		_curAlpha = 0f;
		Visible = false;
		Scale = Vector2.Zero;
		Modulate = new Color(1f, 1f, 1f, 0f);
	}

	public override void _ExitTree()
	{
		// _debugLabel was reparented out of this node's subtree in Init, so
		// it won't auto-free when MobHUD does — free it explicitly here.
		_debugLabel?.QueueFree();
	}

	public override void _Process(double delta)
	{
		using var _prof = Profiler.Sample("MobHUD.Process");
		Vector3 worldPosition = _mob.HudAnchor != null ? _mob.HudAnchor.GlobalPosition : _mob.GlobalPosition;
		bool behindCamera = _camera.IsPositionBehind(worldPosition);
		Vector2 screenPos = behindCamera ? Vector2.Zero : GameClient.Current.ProjectToScreen(worldPosition);

		// Debug label runs on its own — independent of the perception-bar fade
		// so it never gets scaled or modulated by the bar animation. Hidden
		// when the mob is dead, behind the camera, both cvars off, or the
		// breakdown is fully inert (V/H/S all 0 AND no LOS — e.g. burrowed or
		// far-underground mobs that aren't participating in perception this
		// tick). Hiding inert labels stops the world from being cluttered
		// with rows of zeros over mobs the player has no chance of detecting.
		bool cvarEnabled = CVars.debugPlayerPerception.Value || CVars.debugMobPerception.Value;
		PerceptionDebug d = CVars.debugMobPerception.Value ? _mob.mobToPlayerDebug : _mob.playerToMobDebug;
		bool anyActivity = d.vision > 0f || d.hearing > 0f || d.smell > 0f || d.los;
		bool showDebug = _mob.alive && !behindCamera && cvarEnabled && anyActivity;
		if (_debugLabel != null)
		{
			_debugLabel.Visible = showDebug;
			if (showDebug)
			{
				_debugLabel.Text = string.Format(
					"V{0:F2} H{1:F2} S{2:F2}\nL{3:F2} D{4:F2} F{5:F2} S{6:F2} C{7:F2} LOS{8}",
					d.vision, d.hearing, d.smell,
					d.lighting, d.distance, d.facing, d.speed, d.camouflage,
					d.los ? "+" : "-");
				// Center the 160-wide label horizontally on the mob and hover
				// it 64px above so it sits clear of the perception icon.
				_debugLabel.Position = screenPos + new Vector2(-80f, -64f);
			}
		}

		if (behindCamera)
		{
			Visible = false;
			return;
		}

		bool stateHidden = !_mob.alive || _mob.playerPerceptionState == EPlayerPerceptionState.Hidden;
		if (!stateHidden)
		{
			_discoveryBar.Visible = _mob.playerPerceptionState == EPlayerPerceptionState.Detected;
			_perceptionBar.Visible = _mob.mobData.team == ETeam.Hostile && _mob.perception > 0 && !_mob.triggered && _mob.playerCanSee;
			_healthBar.Visible = _mob.mobData.team == ETeam.Hostile && !_discoveryBar.Visible && _mob.triggered && !_mob.burrowed && (_mob.health < _mob.maxHealth || _mob.armor < _mob.maxArmor);
			_armorBar.Visible = _healthBar.Visible && _mob.armor > 0;
		}
		else
		{
			_discoveryBar.Visible = false;
			_perceptionBar.Visible = false;
			_healthBar.Visible = false;
		}

		bool anyBarVisible = _discoveryBar.Visible || _perceptionBar.Visible || _healthBar.Visible;

		// Hostile mobs show perception + health together as soon as perception
		// ticks above 0 — health bar visibility alone doesn't mean "discovered",
		// so we gate scale/alpha on perception state and triggered, not on which
		// bar happens to be on screen.
		float targetScale;
		float targetAlpha;
		bool discovered = _mob.playerPerceptionState == EPlayerPerceptionState.Detected
			|| _mob.playerPerceptionState == EPlayerPerceptionState.Discovered;
		if (!anyBarVisible)
		{
			targetScale = 0f;
			targetAlpha = 0f;
		}
		else if (_mob.playerPerceptionState == EPlayerPerceptionState.Detected)
		{
			targetScale = PerceptionScale;
			targetAlpha = PerceptionAlpha;
		}
		else if (_mob.triggered)
		{
			targetScale = _mob.mobData.hudScale;
			targetAlpha = TriggeredAlpha;
		}
		else
		{
			targetScale = _mob.mobData.hudScale;
			targetAlpha = DiscoveredAlpha;
		}

		float t = 1f - Mathf.Exp(-AnimSpeed * (float)delta);
		_curScale = Mathf.Lerp(_curScale, targetScale, t);
		_curAlpha = Mathf.Lerp(_curAlpha, targetAlpha, t);

		if (targetScale <= 0f && targetAlpha <= 0f && _curScale < HideThreshold && _curAlpha < HideThreshold)
		{
			_curScale = 0f;
			_curAlpha = 0f;
			Visible = false;
			return;
		}

		Visible = true;
		Scale = new Vector2(_curScale, _curScale);
		Modulate = new Color(1f, 1f, 1f, _curAlpha);
		Position = screenPos;
		if (_healthBar != null)
		{
			_healthBar.MinValue = 0;
			_healthBar.MaxValue = _mob.maxHealth;
			_healthBar.Value = _mob.health;
		}
		if (_armorBar != null)
		{
			_armorBar.MinValue = 0;
			_armorBar.MaxValue = _mob.maxArmor;
			_armorBar.Value = _mob.armor;
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
}
