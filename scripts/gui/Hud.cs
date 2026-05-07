using Godot;
using System.Collections.Generic;

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
	[Export] HudSignpostPanel _signpostPanel;
	[Export] HudRegionBanner _regionBanner;
	[Export] TextureRect _minimapTexture;
	[Export] ButtonHint _buttonHintTurnLeft;
	[Export] ButtonHint _buttonHintTurnRight;
	[Export] ButtonHint _buttonHintIndoors;
	[Export] ButtonHint _buttonHintMap;
	Player _player;
	Inventory _inventory;
	// Active status-effect HUD nodes keyed by their data. Multiple stacked
	// instances of the same data show as one HUD entry — count is set on the
	// existing entry instead of spawning duplicates. Entries are added /
	// removed each tick as the player's status-effect list changes.
	readonly Dictionary<StatusEffectData, StatusEffectHud> _statusEffectHuds = new();
	// Reused per-frame so the per-data instance counts don't churn the GC.
	// Cleared at the top of UpdateStatusEffects.
	readonly Dictionary<StatusEffectData, int> _statusEffectCounts = new();
	readonly Dictionary<StatusEffectData, ulong> _statusEffectShortestRemainingMs = new();
	readonly List<StatusEffectData> _statusEffectsToRemove = new();
	float _mapRotation;
	// Lerped minimap view radius (meters), computed each frame from
	// TextureRect size and GameClient.minimapPixelsPerMeter. Damps toward
	// the target so indoor/outdoor mode toggles glide smoothly.
	float _minimapViewRadius;
	const float MinimapViewRadiusLerpRate = 10f;
	// Last-pushed texture references per state slot, so we only call
	// SetShaderParameter when a binding actually changes (mode toggle, slice
	// crossing, or new chunk loaded into the active layer). Per-state scalar
	// uniforms still push every frame — they're cheap and can drift.
	Texture2D _boundTileLut;
	Texture2D _boundFoliageLut;
	struct BoundStateTextures
	{
		public Texture2D Surface, SurfaceBelow1, SurfaceBelow2;
		public Texture2D Exploration, ExplorationBelow1, ExplorationBelow2;
	}
	BoundStateTextures _boundA;
	BoundStateTextures _boundB;

	public override void _Ready()
	{
		gameClient.onPlayerSpawned += OnPlayerSpawned;
		gameClient.onRegionEntered += OnRegionEntered;
		_weaponLeftButtonHint.SetHint("AttackMelee", string.Empty);
		_weaponRightButtonHint.SetHint("AttackRanged", string.Empty);
		_consumableButtonHint.SetHint("UseItem", string.Empty);
		_buttonHintTurnLeft.SetHint("CameraLeft", string.Empty);
		_buttonHintTurnRight.SetHint("CameraRight", string.Empty);
		_buttonHintIndoors.SetHint("CameraDown", string.Empty);
		_buttonHintMap.SetHint("Map", string.Empty);
		if (_signpostPanel != null)
		{
			_signpostPanel.Visible = false;
		}
	}

	public bool IsSignpostOpen => _signpostPanel != null && _signpostPanel.IsOpen;

	public void ShowSignpost(string text)
	{
		_signpostPanel?.Show(text);
	}

	public void CloseSignpost()
	{
		_signpostPanel?.Close();
	}

	public override void _ExitTree()
	{
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned -= OnPlayerSpawned;
			gameClient.onRegionEntered -= OnRegionEntered;
		}
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventorySlotChanged;
			_inventory.onActiveConsumableChanged -= OnActiveConsumableChanged;
		}
	}

	void OnRegionEntered(RegionData region)
	{
		_regionBanner?.Show(region);
	}

	void OnPlayerSpawned(Player player)
	{
		_player = player;
		_inventory = player.Inventory;
		_inventory.onSlotChanged += OnInventorySlotChanged;
		_inventory.onActiveConsumableChanged += OnActiveConsumableChanged;
		RefreshSlot(EInventorySlot.WeaponLeft);
		RefreshSlot(EInventorySlot.WeaponRight);
		RefreshSlot(EInventorySlot.Consumable);
	}

	void OnInventorySlotChanged(EInventorySlot slot)
	{
		RefreshSlot(slot);
	}

	void OnActiveConsumableChanged(int index)
	{
		RefreshSlot(EInventorySlot.Consumable);
	}

	void RefreshSlot(EInventorySlot slot)
	{
		ItemState item = _inventory?.GetEquipped(slot);
		switch (slot)
		{
			case EInventorySlot.WeaponLeft:
				_weaponLeftHud.SetItem(item);
				_weaponLeftButtonHint.Visible = item != null;
				_weaponLeftButtonHint.ActionName = item?.data?.displayName ?? string.Empty;
				break;
			case EInventorySlot.WeaponRight:
				_weaponRightHud.SetItem(item);
				_weaponRightButtonHint.Visible = item != null;
				_weaponRightButtonHint.ActionName = item?.data?.displayName ?? string.Empty;
				break;
			case EInventorySlot.Consumable:
				_consumableHud.SetItem(item);
				_consumableButtonHint.Visible = item != null;
				break;
		}
	}

	public override void _Process(double delta)
	{
		if (_player == null)
		{
			return;
		}

		float maxHealth = _player.MaxHealth;
		_healthBar.MinValue = 0;
		_healthBar.MaxValue = 1;
		_healthBar.Value = maxHealth > 0f ? _player.Health / maxHealth : 0f;

		float maxArmor = _player.MaxArmor;
		_armorBar.MinValue = 0;
		_armorBar.MaxValue = 1;
		_armorBar.Visible = maxArmor > 0f;
		_armorBar.Value = maxArmor > 0f ? _player.Armor / maxArmor : 0f;

		ulong now = gameClient.World?.GameTimeMs ?? 0;
		_weaponLeftHud.Tick(now);
		_weaponRightHud.Tick(now);
		_consumableHud.Tick(now);

		UpdateStatusEffects(now);

		_weaponLeftButtonHint.SetProgress(GetChargeProgress(EInventorySlot.WeaponLeft, now));
		_weaponRightButtonHint.SetProgress(GetChargeProgress(EInventorySlot.WeaponRight, now));
		_consumableButtonHint.SetProgress(GetChargeProgress(EInventorySlot.Consumable, now));

		if (gameClient.camera.RotationDegrees.Y != _mapRotation)
		{
			_mapRotation = gameClient.camera.RotationDegrees.Y;
			_minimapTexture.RotationDegrees = _mapRotation;
		}

		UpdateMinimap();
	}

	// Pushes the minimap's two-state crossfade snapshot into the shader
	// material each frame. State A is the previous mode/slice (decaying);
	// state B is the live one. `state_transition` lerps 0 → 1 so the shader
	// can mix(render(A), render(B), t) for a smooth fade across mode toggles
	// and slice crossings.
	void UpdateMinimap()
	{
		Minimap minimap = gameClient.World?.Minimap;
		if (minimap == null || _minimapTexture == null)
		{
			return;
		}
		if (_minimapTexture.Material is not ShaderMaterial mat)
		{
			return;
		}
		if (minimap.StateB.Surface == null)
		{
			return;
		}

		// LUT textures are bound once and don't churn.
		if (minimap.TileLutTexture != _boundTileLut)
		{
			mat.SetShaderParameter("tile_lut", minimap.TileLutTexture);
			_boundTileLut = minimap.TileLutTexture;
		}
		if (minimap.FoliageLutTexture != _boundFoliageLut)
		{
			mat.SetShaderParameter("foliage_lut", minimap.FoliageLutTexture);
			_boundFoliageLut = minimap.FoliageLutTexture;
		}

		PushState(mat, minimap.StateA, suffix: "_a", ref _boundA);
		PushState(mat, minimap.StateB, suffix: "_b", ref _boundB);

		Vector3 pos = _player?.GlobalPosition ?? Vector3.Zero;
		mat.SetShaderParameter("player_world_xz", new Vector2(pos.X, pos.Z));
		mat.SetShaderParameter("view_radius_meters", UpdateMinimapViewRadius(minimap));
		mat.SetShaderParameter("state_transition", minimap.StateTransition);
	}

	// Computes the visible half-extent (meters) for the minimap shader.
	// Independent of player vision: the TextureRect's screen-pixel size
	// divided by GameClient.minimapPixelsPerMeter gives the world meters
	// the rect covers; halve for the radius. Indoor mode multiplies
	// pixels-per-meter by minimapIndoorZoom so corridors zoom in. Damp-
	// lerps toward the target so mode toggles glide smoothly.
	float UpdateMinimapViewRadius(Minimap minimap)
	{
		GameClient gc = gameClient;
		float ppm = gc?.minimapPixelsPerMeter ?? 2f;
		if (minimap.Mode == Minimap.EMinimapMode.Indoor)
		{
			ppm *= gc?.minimapIndoorZoom ?? 2f;
		}
		float screenPx = Mathf.Min(_minimapTexture.Size.X, _minimapTexture.Size.Y);
		float target = (screenPx / ppm) * 0.5f;
		if (_minimapViewRadius <= 0f)
		{
			_minimapViewRadius = target;
		}
		else
		{
			float t = 1f - Mathf.Exp(-MinimapViewRadiusLerpRate * (float)GetProcessDeltaTime());
			_minimapViewRadius = Mathf.Lerp(_minimapViewRadius, target, t);
		}
		return _minimapViewRadius;
	}

	// Per-state uniform writer. Texture uniforms are change-detected to
	// avoid re-binding the same Godot texture every frame. Per-state
	// scalars / vectors are pushed every frame since they can drift (e.g.
	// reference_elevation in outdoor mode tracks the player's eye height).
	void PushState(ShaderMaterial mat, in Minimap.StateSnapshot s, string suffix, ref BoundStateTextures bound)
	{
		Texture2D surf = s.Surface;
		Texture2D below1 = s.SurfaceBelow1 ?? surf;
		Texture2D below2 = s.SurfaceBelow2 ?? surf;
		Texture2D expl = s.Exploration ?? surf;
		Texture2D explBelow1 = s.ExplorationBelow1 ?? expl;
		Texture2D explBelow2 = s.ExplorationBelow2 ?? expl;

		if (surf != bound.Surface) { mat.SetShaderParameter("surface_texture" + suffix, surf); bound.Surface = surf; }
		if (below1 != bound.SurfaceBelow1) { mat.SetShaderParameter("surface_texture_below1" + suffix, below1); bound.SurfaceBelow1 = below1; }
		if (below2 != bound.SurfaceBelow2) { mat.SetShaderParameter("surface_texture_below2" + suffix, below2); bound.SurfaceBelow2 = below2; }
		if (expl != bound.Exploration) { mat.SetShaderParameter("exploration_texture" + suffix, expl); bound.Exploration = expl; }
		if (explBelow1 != bound.ExplorationBelow1) { mat.SetShaderParameter("exploration_texture_below1" + suffix, explBelow1); bound.ExplorationBelow1 = explBelow1; }
		if (explBelow2 != bound.ExplorationBelow2) { mat.SetShaderParameter("exploration_texture_below2" + suffix, explBelow2); bound.ExplorationBelow2 = explBelow2; }

		mat.SetShaderParameter("world_origin_xz" + suffix, new Vector2(s.WorldOriginXZ.X, s.WorldOriginXZ.Y));
		mat.SetShaderParameter("world_extent_pixels" + suffix, s.ExtentPixels);
		mat.SetShaderParameter("meters_per_pixel" + suffix, s.MetersPerPixel);
		mat.SetShaderParameter("reference_elevation" + suffix, s.ReferenceElevation);
	}

	// Sync the strip of status-effect icons against the player's current list.
	// Effects with the same data stack into one entry whose count badge shows
	// stack size; the progress bar tracks the timer of the instance closest to
	// expiry (or hides if every instance in the stack is persistent).
	void UpdateStatusEffects(ulong now)
	{
		_statusEffectCounts.Clear();
		_statusEffectShortestRemainingMs.Clear();

		IReadOnlyList<StatusEffectState> effects = _player.StatusEffects;
		for (int i = 0; i < effects.Count; i++)
		{
			StatusEffectState s = effects[i];
			if (s?.data == null)
			{
				continue;
			}
			_statusEffectCounts.TryGetValue(s.data, out int prevCount);
			_statusEffectCounts[s.data] = prevCount + 1;
			if (s.IsTimed)
			{
				ulong remaining = s.RemainingMs(now);
				if (!_statusEffectShortestRemainingMs.TryGetValue(s.data, out ulong prevShortest)
					|| remaining < prevShortest)
				{
					_statusEffectShortestRemainingMs[s.data] = remaining;
				}
			}
		}

		// Drop HUD entries whose data no longer appears in the player's list.
		_statusEffectsToRemove.Clear();
		foreach (var kv in _statusEffectHuds)
		{
			if (!_statusEffectCounts.ContainsKey(kv.Key))
			{
				kv.Value.QueueFree();
				_statusEffectsToRemove.Add(kv.Key);
			}
		}
		for (int i = 0; i < _statusEffectsToRemove.Count; i++)
		{
			_statusEffectHuds.Remove(_statusEffectsToRemove[i]);
		}

		// Add / refresh entries for everything currently held.
		foreach (var kv in _statusEffectCounts)
		{
			StatusEffectData data = kv.Key;
			int count = kv.Value;
			if (!_statusEffectHuds.TryGetValue(data, out StatusEffectHud hud))
			{
				hud = _statusEffectHudScene.Instantiate<StatusEffectHud>();
				_statusEffectContainer.AddChild(hud);
				_statusEffectHuds[data] = hud;
			}
			bool hasTimer = _statusEffectShortestRemainingMs.TryGetValue(data, out ulong shortestRemaining);
			float progress = 0f;
			if (hasTimer)
			{
				float totalMs = data.duration * 1000f;
				progress = totalMs > 0f ? shortestRemaining / totalMs : 0f;
			}
			hud.Set(data, count, progress, hasTimer);
		}
	}

	// Charge fill toward the next tier's chargeTime while the slot's item is
	// in the runner's Charging phase. Cooldown is shown by WeaponHud, not here.
	float GetChargeProgress(EInventorySlot slot, ulong nowMs)
	{
		ItemState item = _inventory?.GetEquipped(slot);
		if (item == null || _player == null || _player.Runner == null)
		{
			return 0f;
		}
		ref readonly PlayerAction action = ref _player.Runner.Current;
		if (action.phase != EActionPhase.Charging)
		{
			return 0f;
		}
		if (action.context.primaryItem != item)
		{
			return 0f;
		}
		ItemActionProfile profile = action.profile;
		if (profile == null || profile.chargedActions == null || profile.chargedActions.Count == 0)
		{
			return 0f;
		}


		float elapsed = (nowMs - action.pressMs) / 1000f;
		int nextIndex = action.selectedTierIndex + 1;
		if (nextIndex >= profile.chargedActions.Count)
		{
			return 1f;
		}
		ItemAction nextTier = profile.chargedActions[nextIndex];
		if (nextTier == null)
		{
			return 1f;
		}
		float prevChargeTime = action.selectedTierIndex >= 0
			? profile.chargedActions[action.selectedTierIndex].chargeTime
			: 0f;
		float span = nextTier.chargeTime - prevChargeTime;
		if (span <= 0f)
		{
			return 1f;
		}
		return Mathf.Clamp((elapsed - prevChargeTime) / span, 0f, 1f);
	}
}
