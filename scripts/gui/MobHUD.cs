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
		// Status effects sit below the bars and need to stay visible whenever
		// the mob itself is visible — independent of the bar fade and the
		// behind-camera/no-bar early returns that hide MobHUD. Reparent it
		// out of MobHUD's subtree (same trick as _debugLabel) so the
		// perception-bar Scale/Modulate cascade can't reach it; position is
		// driven manually from the projected mob position each tick.
		if (_statusEffectContainer != null && parent != null)
		{
			_statusEffectContainer.Reparent(parent);
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
		_mob.TreeExiting += QueueFree;
		_curScale = 0f;
		_curAlpha = 0f;
		Visible = false;
		Scale = Vector2.Zero;
		Modulate = new Color(1f, 1f, 1f, 0f);
	}

	public override void _ExitTree()
	{
		// _debugLabel and _statusEffectContainer were reparented out of this
		// node's subtree in Init, so they won't auto-free when MobHUD does —
		// free them explicitly here.
		_debugLabel?.QueueFree();
		_statusEffectContainer?.QueueFree();
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
		bool perceptionCvar = CVars.debugPlayerPerception.Value || CVars.debugMobPerception.Value;
		bool positionCvar = CVars.debugMobPosition.Value;
		bool cvarEnabled = perceptionCvar || positionCvar;
		PerceptionDebug d = CVars.debugMobPerception.Value ? _mob.mobToPlayerDebug : _mob.playerToMobDebug;
		bool anyActivity = d.vision > 0f || d.hearing > 0f || d.smell > 0f || d.los;
		// Position rows render unconditionally when the position cvar is on
		// so a stationary, fully-occluded mob (no V/H/S activity) still
		// shows up — that's exactly the case we want to inspect.
		bool showDebug = _mob.alive && !behindCamera && cvarEnabled && (positionCvar || anyActivity);
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
						d.los ? "+" : "-");
				}
				if (positionCvar)
				{
					Vector3 mobPos = _mob.GlobalPosition;
					string posLine = string.Format("Pos {0:F2},{1:F2},{2:F2}", mobPos.X, mobPos.Y, mobPos.Z);
					text = text.Length > 0 ? text + "\n" + posLine : posLine;
				}
				_debugLabel.Text = text;
				// Center the 160-wide label horizontally on the mob and hover
				// it 64px above so it sits clear of the perception icon.
				_debugLabel.Position = screenPos + new Vector2(-80f, -64f);
			}
		}

		// Status effects render independently of the perception-bar fade —
		// they're tied to the mob being visible to the player, not to whether
		// the perception/health bar happens to be on screen this tick. The
		// container was reparented out of MobHUD in Init so it's not affected
		// by the Scale/Modulate cascade or the early returns below.
		if (_statusEffectContainer != null)
		{
			bool statusVisible = !behindCamera && _mob.alive && _mob.playerPerceptionState != EPlayerPerceptionState.Hidden;
			_statusEffectContainer.Visible = statusVisible;
			if (statusVisible)
			{
				// Centered above the mob anchor, clearing the health bar (whose
				// top sits ~23px * hudScale above the anchor). Screen +Y is down,
				// so the strip rides a negative Y offset to sit above the bar.
				_statusEffectContainer.Position = screenPos + new Vector2(-40f, -48f);
				UpdateStatusEffects();
			}
		}

		if (behindCamera)
		{
			Visible = false;
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
