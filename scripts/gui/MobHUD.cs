using System.Collections.Generic;
using Godot;

public partial class MobHUD : Node2D
{
	const float PerceptionScale = 0.5f;
	const float PerceptionAlpha = 0.5f;
	const float DiscoveredAlpha = 0.5f;
	const float TriggeredAlpha = 0.75f;
	const float HideThreshold = 0.01f;
	const float AnimSpeed = 12f;

	// The fade/scale subtree: the perception/health bars + frame. Only this node
	// is scaled and modulated by the perception animation (and hidden when no bar
	// is on screen) — the root stays full-size and visible so the debug label and
	// status strip, ordinary children riding their authored offsets, follow their
	// own visibility rules.
	[Export] private Node2D _bars;
	[Export] private TextureProgressBar _healthBar;
	[Export] private TextureProgressBar _armorBar;
	[Export] private TextureProgressBar _perceptionBar;
	[Export] private TextureProgressBar _discoveryBar;
	[Export] private Label _debugLabel;
	[Export] private BoxContainer _statusEffectContainer;
	[Export] private PackedScene _statusEffectIconScene;
	// Badge nested inside the health bar showing the mob's descriptor badge icon
	// (MobSimState.Badge). It's a child of the health bar in the scene, so it
	// rides the bar's visibility — it shows exactly when the badged mob's health
	// bar is on screen. Hidden (and untextured) for mobs with no badge.
	[Export] private TextureRect _eliteStatusIcon;
	// Difficulty pips: one shown per mob level (Level+1 total). The strip rides
	// the same perceived/on-screen visibility as the status-effect strip below.
	[Export] private Control _levelContainer;
	[Export] private Godot.Collections.Array<TextureRect> _levelPips;
	// Pips fan out along a downward arc ("smile") centered on the HUD circle, so
	// the row stays symmetric no matter how many are lit. Radius is in the (scale-1)
	// circle's space; the container is scaled each frame to match the circle's
	// display scale (see _Process), so the pips ride the circle's edge.
	[Export] private float _pipArcRadius = 20f;
	// Angular gap between adjacent pips (the total fan = this × (count − 1)).
	[Export(PropertyHint.Range, "0,90,1")] private float _pipArcSpacingDegrees = 32f;

	// One icon per active StatusEffectState — multiple stacks of the same data
	// show as multiple icons side-by-side. New entries play the intro animation
	// (fade + shrink from 3x to 1x) once, then sit at full opacity until the
	// matching StatusEffectState falls off the mob, at which point Outro() is
	// called and the icon fades out before being freed.
	readonly Dictionary<StatusEffectState, StatusEffectIcon> _statusEffectIcons = new();
	readonly HashSet<StatusEffectState> _statusEffectsThisTick = new();
	readonly List<StatusEffectState> _statusEffectsToRemove = new();

	Camera3D _camera;
	Mob _mob;
	float _curScale;
	float _curAlpha;
	// Whether this mob shows level pips at all — dangerous mobs (combat threats)
	// only. Companions, villagers, and prey never surface them. Fixed at spawn.
	bool _showLevelPips;

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
		// Badge the health bar with the mob's marker icon, authored on its
		// MobDescriptor (MobDescriptor.badge). Fixed at spawn, so resolve the
		// texture + visibility once here rather than per frame; the icon then
		// tracks the health bar's visibility automatically as a child of it in
		// the scene.
		if (_eliteStatusIcon != null)
		{
			Texture2D badge = _mob.Badge;
			_eliteStatusIcon.Visible = badge != null;
			if (badge != null)
			{
				_eliteStatusIcon.Texture = badge;
			}
		}
		// Level pips are fixed at spawn (Level is immutable), so light up Level+1
		// of them once here; the container's on-screen visibility is toggled in
		// _Process. Only dangerous mobs show them at all.
		_showLevelPips = _mob.mobData?.dangerous ?? false;
		if (_levelPips != null)
		{
			int pipCount = _mob.Level + 1;
			for (int i = 0; i < _levelPips.Count; i++)
			{
				if (_levelPips[i] != null)
				{
					_levelPips[i].Visible = i < pipCount;
				}
			}
			LayoutPipArc(pipCount);
		}
		// Hidden until _Process places the root and resolves on-screen visibility;
		// otherwise the lit pips flash at the screen's top-left (the root's default
		// (0,0) position) for the frame before the first _Process runs. Matches how
		// _bars is hidden at spawn below.
		if (_levelContainer != null)
		{
			_levelContainer.Visible = false;
		}
		_mob.TreeExiting += QueueFree;
		_curScale = 0f;
		_curAlpha = 0f;
		_bars.Visible = false;
		_bars.Scale = Vector2.Zero;
		_bars.Modulate = new Color(1f, 1f, 1f, 0f);
	}

	// Spreads the `count` visible pips evenly across a downward arc, centered on
	// straight-down so the row is symmetric for any count. Positions are local to
	// the pip container, so they scale with a scaled ancestor. Called once at
	// spawn (pip count is immutable).
	void LayoutPipArc(int count)
	{
		if (_levelPips == null || count <= 0)
		{
			return;
		}
		float step = Mathf.DegToRad(_pipArcSpacingDegrees);
		// Screen +Y is down, so straight-down is +90°; pips fan out around it. The
		// fan is (count-1) gaps wide, so start half of it left of center (a single
		// pip gets a zero-wide fan and lands dead-center).
		const float centerAngle = Mathf.Pi * 0.5f;
		float startAngle = centerAngle - step * (count - 1) * 0.5f;
		for (int i = 0; i < count && i < _levelPips.Count; i++)
		{
			TextureRect pip = _levelPips[i];
			if (pip == null)
			{
				continue;
			}
			float angle = startAngle + step * i;
			Vector2 arcCenter = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * _pipArcRadius;
			// TextureRect.Position is its top-left; offset by half its size to
			// center the pip on the arc point.
			pip.Position = arcCenter - pip.CustomMinimumSize * 0.5f;
		}
	}

	public override void _Process(double delta)
	{
		using var _prof = Profiler.Sample("MobHUD.Process");
		Vector3 worldPosition = _mob.hudAnchor != null ? _mob.hudAnchor.GlobalPosition : _mob.GlobalPosition;
		bool behindCamera = _camera.IsPositionBehind(worldPosition);
		Vector2 screenPos = behindCamera ? Vector2.Zero : GameClient.Current.ProjectToScreen(worldPosition);

		// The root tracks the mob every frame and is never scaled, modulated, or
		// hidden — only the _bars subtree is. The debug label and status strip are
		// ordinary children riding their authored offsets relative to this
		// position, so they stay full-size and follow their own visibility rules.
		Position = screenPos;

		// Debug label runs on its own — independent of the perception-bar fade
		// so it never gets scaled or modulated by the bar animation. When a debug
		// cvar is on, the label shows for EVERY live, on-screen mob regardless of
		// activity — a mob you can't perceive (V/H/S all 0, LOS ?) is exactly the
		// one you want to inspect to see WHY (lighting, distance, floor). The mob
		// being non-visible no longer hides its readout.
		bool perceptionCvar = CVars.debugPlayerPerception.Value || CVars.debugMobPerception.Value;
		bool positionCvar = CVars.debugMobPosition.Value;
		bool cvarEnabled = perceptionCvar || positionCvar;
		PerceptionDebug d = CVars.debugMobPerception.Value ? _mob.mobToPlayerDebug : _mob.playerToMobDebug;
		bool showDebug = _mob.alive && !behindCamera && cvarEnabled;
		if (_debugLabel != null)
		{
			_debugLabel.Visible = showDebug;
			if (showDebug)
			{
				string text = "";
				if (perceptionCvar)
				{
					text = string.Format(
						"V{0:F2} H{1:F2} S{2:F2}\nL{3:F2} D{4:F2} F{5:F2} S{6:F2} C{7:F2} LOS{8}",
						d.vision, d.hearing, d.smell,
						d.lighting, d.distance, d.facing, d.speed, d.camouflage,
						d.los switch { EPerceptionLos.Clear => "+", EPerceptionLos.Blocked => "-", _ => "?" });
				}
				if (positionCvar)
				{
					Vector3 mobPos = _mob.GlobalPosition;
					string posLine = string.Format("Pos {0:F2},{1:F2},{2:F2}", mobPos.X, mobPos.Y, mobPos.Z);
					text = text.Length > 0 ? text + "\n" + posLine : posLine;
				}
				_debugLabel.Text = text;
			}
		}

		// Status effects render independently of the perception-bar fade —
		// they're tied to the mob being visible to the player, not to whether
		// the perception/health bar happens to be on screen this tick. As a
		// direct child of the (unscaled, unmodulated) root they ride their
		// authored offset and are untouched by the _bars fade below.
		bool perceivedOnScreen = !behindCamera && _mob.alive && _mob.playerPerceptionState != EPlayerPerceptionState.Hidden;
		if (_statusEffectContainer != null)
		{
			_statusEffectContainer.Visible = perceivedOnScreen;
			if (perceivedOnScreen)
			{
				UpdateStatusEffects();
			}
		}
		bool statusStripShowing = perceivedOnScreen && _statusEffectIcons.Count > 0;

		if (behindCamera)
		{
			_bars.Visible = false;
			if (_levelContainer != null)
			{
				_levelContainer.Visible = false;
			}
			return;
		}

		bool stateHidden = !_mob.alive || _mob.playerPerceptionState == EPlayerPerceptionState.Hidden;
		// Effective team so a tamed companion (authored Prey, Friendly once
		// tamed) is treated as player-side rather than reading as prey.
		ETeam hudTeam = _mob.ActorTeam;
		bool playerSide = Teams.AreAllied(hudTeam, ETeam.Player);
		// Health bar shows only when injured (health or armor below max).
		bool injured = _mob.health < _mob.maxHealth || _mob.armor < _mob.maxArmor;
		if (playerSide)
		{
			// A tamed companion is "ours" — there's no stalk/discovery framing,
			// so it never shows the perception or discovery bars. Its health bar
			// stays on screen whenever it's alive and wounded, regardless of
			// perception state or line of sight, so the player can always see a
			// hurt pet's health.
			_discoveryBar.Visible = false;
			_perceptionBar.Visible = false;
			_healthBar.Visible = _mob.alive && !_mob.burrowed && injured;
			_armorBar.Visible = _healthBar.Visible && _mob.armor > 0;
		}
		else if (!stateHidden)
		{
			_discoveryBar.Visible = _mob.playerPerceptionState == EPlayerPerceptionState.Detected;
			// Hostiles and prey both surface the stealth (perception) and health
			// bars — you stalk both. Other teams (neutral) show neither.
			bool combatOrPrey = hudTeam == ETeam.Hostile || hudTeam == ETeam.Prey;
			_perceptionBar.Visible = combatOrPrey && _mob.perception > 0 && !_mob.triggered && _mob.playerCanSee;
			// Hostiles reveal the health bar once engaged (triggered); prey reveal
			// it whenever wounded, so you can track a hurt animal you're hunting.
			// Other teams don't show one.
			bool healthEligible = hudTeam switch
			{
				ETeam.Hostile => _mob.triggered,
				ETeam.Prey => true,
				_ => false,
			};
			_healthBar.Visible = healthEligible && !_discoveryBar.Visible && !_mob.burrowed && injured;
			_armorBar.Visible = _healthBar.Visible && _mob.armor > 0;
		}
		else
		{
			_discoveryBar.Visible = false;
			_perceptionBar.Visible = false;
			_healthBar.Visible = false;
		}

		bool anyBarVisible = _discoveryBar.Visible || _perceptionBar.Visible || _healthBar.Visible;

		// Level pips (dangerous mobs only) show only alongside another HUD
		// element — any perception/health/discovery bar or a live status icon —
		// so they never float over the mob on their own. Set here (after bar
		// visibility resolves) rather than at the fade tail so they track the
		// logical bar state, not the fade-out.
		if (_levelContainer != null)
		{
			_levelContainer.Visible = _showLevelPips && perceivedOnScreen && (anyBarVisible || statusStripShowing);
			// Match the circle's steady display scale so the arc rides its edge. Use
			// the target scale (Detected → PerceptionScale, else hudScale), not the
			// animating _curScale, so the pips stay full-size instead of riding the
			// fade — and stay non-zero when only the status strip is up (no bar).
			float pipScale = _mob.playerPerceptionState == EPlayerPerceptionState.Detected
				? PerceptionScale
				: (_mob.mobData?.hudScale ?? 1f);
			_levelContainer.Scale = new Vector2(pipScale, pipScale);
		}

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
			_bars.Visible = false;
			return;
		}

		_bars.Visible = true;
		_bars.Scale = new Vector2(_curScale, _curScale);
		_bars.Modulate = new Color(1f, 1f, 1f, _curAlpha);
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

	// Per-instance icon strip: one StatusEffectIcon per StatusEffectState on
	// the mob. New states play the intro animation (fade + shrink) once; states
	// that have fallen off the mob since last tick are handed Outro() so they
	// fade out before being freed. The icon's own _Process drives the timing.
	void UpdateStatusEffects()
	{
		if (_statusEffectContainer == null || _statusEffectIconScene == null)
		{
			return;
		}
		_statusEffectsThisTick.Clear();
		IReadOnlyList<StatusEffectState> effects = _mob.StatusEffects;
		for (int i = 0; i < effects.Count; i++)
		{
			StatusEffectState s = effects[i];
			// Only Transient effects ride the fading strip. Elite signatures show
			// in the health-bar badge instead; Permanent quirks aren't surfaced
			// on the mob HUD at all.
			if (s?.data == null || s.data.icon == null || (s.data.category & EEffectCategory.Transient) == 0)
			{
				continue;
			}
			_statusEffectsThisTick.Add(s);
			if (!_statusEffectIcons.ContainsKey(s))
			{
				StatusEffectIcon icon = _statusEffectIconScene.Instantiate<StatusEffectIcon>();
				_statusEffectContainer.AddChild(icon);
				icon.Init(s.data, autoOutro: false);
				_statusEffectIcons[s] = icon;
			}
		}

		_statusEffectsToRemove.Clear();
		foreach (var kv in _statusEffectIcons)
		{
			StatusEffectIcon icon = kv.Value;
			if (!_statusEffectsThisTick.Contains(kv.Key))
			{
				icon.Outro();
			}
			if (icon.IsFinished)
			{
				icon.QueueFree();
				_statusEffectsToRemove.Add(kv.Key);
			}
		}
		for (int i = 0; i < _statusEffectsToRemove.Count; i++)
		{
			_statusEffectIcons.Remove(_statusEffectsToRemove[i]);
		}
	}
}
