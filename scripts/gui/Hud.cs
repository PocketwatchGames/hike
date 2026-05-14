using Godot;
using System.Collections.Generic;

public partial class Hud : Control
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
	[Export] ProgressBar _staminaBar;
	[Export] HudSignpostPanel _signpostPanel;
	[Export] DialogueController _dialoguePanel;
	[Export] HudRegionBanner _regionBanner;
	[Export] TextureRect _minimapTexture;
	[Export] ButtonHint _buttonHintTurnLeft;
	[Export] ButtonHint _buttonHintTurnRight;
	[Export] ButtonHint _buttonHintIndoors;
	[Export] ButtonHint _buttonHintMap;
	[Export] TextureRect _weatherDay;
	[Export] TextureRect _weatherNight;
	[Export] Control _weatherContainer;
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

	// Weather widget — clock-face container holds a day icon and a night
	// icon that crossfade across sunrise/sunset, with the container itself
	// rotating once per game-day so the icons sweep around like a celestial
	// dial. The 3×3 texture grid keys on temperature class (cold/normal/hot
	// against the player's authored thresholds) and cloud cover class
	// (clear/cloudy/overcast).
	Texture2D[,] _weatherDayTextures;
	Texture2D[,] _weatherNightTextures;
	Texture2D _boundWeatherDayTexture;
	Texture2D _boundWeatherNightTexture;
	// Working WeatherData / ZoneData reused each frame for the forecast
	// blend + diurnal eval. Allocated lazily on first valid frame so HUD
	// construction order stays cheap.
	WeatherData _forecastEnvelope;
	WeatherData _forecastDayPeak;
	WeatherData _forecastNightTrough;
	ZoneData _forecastZone;
	// Cloud cover thresholds for the icon's three-stop classification.
	// Authored deserts run near 0, overcast biomes push past 0.7 — splitting
	// the [0, 1] range into rough thirds reads as "few clouds / scattered /
	// blanketed" without flickering between classes on a quiet day.
	const float CloudCloudyThreshold = 0.33f;
	const float CloudOvercastThreshold = 0.66f;
	// Cave fade: sunlight BFS at the player's voxel, smoothstepped to drive
	// the icons to zero alpha when the player descends below the sun's reach.
	// 0.25 of MAX_LIGHT is a soft threshold — a few voxels under an overhang
	// still shows weather; a proper cave drops it cleanly.
	const float CaveFadeSunlightFloor = 0.0f;
	const float CaveFadeSunlightFull = 0.25f;

	public override void _Ready()
	{
		gameClient.onPlayerSpawned += OnPlayerSpawned;
		gameClient.onRegionEntered += OnRegionEntered;
		_signpostPanel.gameClient = gameClient;
		if (_dialoguePanel != null)
		{
			_dialoguePanel.gameClient = gameClient;
		}
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
		LoadWeatherTextures();
	}

	// 18 weather-icon textures keyed by phase × temp × cloud. Loaded once
	// here so per-frame icon swaps are dictionary-free index lookups.
	void LoadWeatherTextures()
	{
		_weatherDayTextures = new Texture2D[3, 3];
		_weatherNightTextures = new Texture2D[3, 3];
		string[] tempLabels = { "cold", "normal", "hot" };
		string[] cloudLabels = { "clear", "cloudy", "overcast" };
		for (int t = 0; t < 3; t++)
		{
			for (int c = 0; c < 3; c++)
			{
				_weatherDayTextures[t, c] = GD.Load<Texture2D>(
					$"res://assets/textures/weather/day_{tempLabels[t]}_{cloudLabels[c]}.png");
				_weatherNightTextures[t, c] = GD.Load<Texture2D>(
					$"res://assets/textures/weather/night_{tempLabels[t]}_{cloudLabels[c]}.png");
			}
		}
	}

	public bool IsSignpostOpen => _signpostPanel != null && _signpostPanel.IsOpen;

	public void ShowSignpost(string text, IInteractive source)
	{
		_signpostPanel?.Open(text, source);
	}

	public void CloseSignpost()
	{
		_signpostPanel?.Close();
	}

	public bool IsDialogueOpen => _dialoguePanel != null && _dialoguePanel.IsOpen;

	public void ShowDialogue(System.Collections.Generic.IReadOnlyList<string> lines, System.Action onClose = null)
	{
		_dialoguePanel?.Show(lines, onClose);
	}

	public void CloseDialogue()
	{
		_dialoguePanel?.Close();
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

		float maxStamina = _player.MaxStamina;
		_staminaBar.MinValue = 0;
		_staminaBar.MaxValue = 1;
		_staminaBar.Visible = maxStamina > 0f;
		_staminaBar.Value = maxStamina > 0f ? _player.Stamina / maxStamina : 0f;

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
		UpdateWeatherWidget();
	}

	// Drive the clock-face weather widget: container rotation, the
	// day/night icon crossfade across sunrise/sunset, icon selection from
	// the forecasted peak-of-day / trough-of-night weather, and an
	// alpha-fade when the player is buried far enough underground that no
	// sunlight reaches their voxel.
	//
	// Rotation: 135° at sunrise, -45° at sunset → 360°/day clockwise.
	// Day icon alpha:
	//   [0, dayFadeInStart]            : 0
	//   [dayFadeInStart, sunriseEnd]   : 0 → 1
	//   [sunriseEnd, sunsetStart]      : 1
	//   [sunsetStart, sunsetEnd]       : 1 → 0
	//   [sunsetEnd, 1]                 : 0
	// Night icon alpha wraps midnight (mirror of day, opposite phase).
	// dayFadeInStart = halfway midnight → sunrise window start.
	// nightFadeInStart = halfway noon → sunset window start.
	void UpdateWeatherWidget()
	{
		if (_weatherContainer == null || _weatherDay == null || _weatherNight == null)
		{
			return;
		}
		WorldState ws = gameClient?.World?.WorldState;
		SimData sim = ws?.SimData;
		if (ws == null || sim == null)
		{
			return;
		}

		float tod = (float)ws.TimeOfDay01;
		float halfWidth = sim.VarianceCrossfadeHalfWidth01;
		const float SunriseCenter = 0.25f;
		const float SunsetCenter = 0.75f;
		float sunriseStart = SunriseCenter - halfWidth;
		float sunriseEnd = SunriseCenter + halfWidth;
		float sunsetStart = SunsetCenter - halfWidth;
		float sunsetEnd = SunsetCenter + halfWidth;
		float dayFadeInStart = 0.5f * sunriseStart;
		float nightFadeInStart = 0.5f * (0.5f + sunsetStart);

		_weatherContainer.RotationDegrees = 135f - 360f * (tod - SunriseCenter);

		float dayAlpha = ComputeDayIconAlpha(tod, dayFadeInStart, sunriseEnd, sunsetStart, sunsetEnd);
		float nightAlpha = ComputeNightIconAlpha(tod, sunriseStart, sunriseEnd, nightFadeInStart, sunsetEnd);

		Vector3 pos = _player?.GlobalPosition ?? Vector3.Zero;
		int sunBfs = ws.GetSunlightWorld(
			Mathf.FloorToInt(pos.X), Mathf.FloorToInt(pos.Y), Mathf.FloorToInt(pos.Z));
		float sunMask = Mathf.Clamp((float)sunBfs / LightEngine.MAX_LIGHT, 0f, 1f);
		float caveFade = Mathf.SmoothStep(CaveFadeSunlightFloor, CaveFadeSunlightFull, sunMask);

		_weatherDay.Modulate = new Color(1f, 1f, 1f, dayAlpha * caveFade);
		_weatherNight.Modulate = new Color(1f, 1f, 1f, nightAlpha * caveFade);

		// Forecast peak / trough weather. Re-blend the zone envelope each
		// frame at the player's XZ so zone crossings update the icon
		// without a manual refresh.
		if (_forecastEnvelope == null)
		{
			_forecastEnvelope = new WeatherData();
			_forecastDayPeak = new WeatherData();
			_forecastNightTrough = new WeatherData();
			_forecastZone = new ZoneData();
		}
		ZoneBlend.Sample(pos, ws, _forecastZone, _forecastEnvelope, out _, out float elevation);
		CopyWeather(_forecastEnvelope, _forecastDayPeak);
		CopyWeather(_forecastEnvelope, _forecastNightTrough);

		// Pick the variance source per icon. Phase 0 (the daytime period
		// starting at sunrise) is even; phase 1 (the night) is odd. The
		// current phase's settled variance lives in *VarianceCur; the
		// upcoming phase's is pre-rolled into *VarianceNext, so the
		// pre-dawn day-icon fade-in can already classify with tomorrow's
		// daytime variance instead of the night's. Slope is 0 — the icon
		// shows the steady-state peak/trough, not a mid-handover lerp.
		//
		// Fade-out latching: during the icon's fade-out window the
		// handover has already happened (it lands at the window start),
		// so *VarianceCur now holds the INCOMING phase's variance and a
		// fresh roll lives in *VarianceNext. Reading either would pop
		// the retiring icon to a different classification at the moment
		// of handover. The retired phase's variance is sitting in
		// *VariancePrev, so the fading-out icon reads from there and
		// keeps its old classification all the way to alpha 0.
		long curPhase = WeatherSimulation.CurrentPhase(ws.TimeOfDayAbsolute, sim);
		bool inDayPhase = (curPhase & 1L) == 0L;
		bool dayFadingOut = tod >= sunsetStart && tod < sunsetEnd;
		bool nightFadingOut = tod >= sunriseStart && tod < sunriseEnd;

		float dayWeatherVar = dayFadingOut ? ws.WeatherVariancePrev
			: inDayPhase ? ws.WeatherVarianceCur : ws.WeatherVarianceNext;
		float dayHumidityVar = dayFadingOut ? ws.HumidityVariancePrev
			: inDayPhase ? ws.HumidityVarianceCur : ws.HumidityVarianceNext;
		float dayCloudVar = dayFadingOut ? ws.CloudVariancePrev
			: inDayPhase ? ws.CloudVarianceCur : ws.CloudVarianceNext;
		float nightWeatherVar = nightFadingOut ? ws.WeatherVariancePrev
			: inDayPhase ? ws.WeatherVarianceNext : ws.WeatherVarianceCur;
		float nightHumidityVar = nightFadingOut ? ws.HumidityVariancePrev
			: inDayPhase ? ws.HumidityVarianceNext : ws.HumidityVarianceCur;
		float nightCloudVar = nightFadingOut ? ws.CloudVariancePrev
			: inDayPhase ? ws.CloudVarianceNext : ws.CloudVarianceCur;

		WeatherSimulation.Apply(_forecastDayPeak, _forecastZone, elevation, sim,
			sim.DiurnalPeak01, dayWeatherVar, 0f, dayHumidityVar, dayCloudVar);
		WeatherSimulation.Apply(_forecastNightTrough, _forecastZone, elevation, sim,
			sim.DiurnalTrough01, nightWeatherVar, 0f, nightHumidityVar, nightCloudVar);

		PlayerData pd = _player?.data;
		int dayTemp = ClassifyTemp(_forecastDayPeak, pd, includeSun: true);
		int dayCloud = ClassifyCloud(_forecastDayPeak.cloudCover);
		int nightTemp = ClassifyTemp(_forecastNightTrough, pd, includeSun: false);
		int nightCloud = ClassifyCloud(_forecastNightTrough.cloudCover);

		Texture2D dayTex = _weatherDayTextures[dayTemp, dayCloud];
		Texture2D nightTex = _weatherNightTextures[nightTemp, nightCloud];
		if (dayTex != _boundWeatherDayTexture) { _weatherDay.Texture = dayTex; _boundWeatherDayTexture = dayTex; }
		if (nightTex != _boundWeatherNightTexture) { _weatherNight.Texture = nightTex; _boundWeatherNightTexture = nightTex; }
	}

	// Classify forecast weather the same way Player.TickBodyTemperature
	// triggers cold/hot status, so the icon flips at the same moment the
	// status effect would. Wind chill shifts BOTH thresholds upward
	// (matching `coldThreshold = data.coldTemperature + windEffect`), and
	// the day icon adds the sun's radiant contribution under cloud
	// attenuation because the player's acclimated body temp at peak
	// includes that bake. Resistances aren't applied — the icon answers
	// "would an unprotected player feel cold/hot in this weather?", which
	// is what the player can plan around. Fog (a derived value) is left
	// out of the sky transmission term for simplicity.
	static int ClassifyTemp(WeatherData forecast, PlayerData pd, bool includeSun)
	{
		float coldT = pd?.coldTemperature ?? 50f;
		float hotT = pd?.hotTemperature ?? 90f;
		float windRed = pd?.windTemperatureReduction ?? 0.5f;
		float windEffect = forecast.windSpeed * windRed;
		float perceived = forecast.airTemperature;
		if (includeSun)
		{
			float skyTransmission = 1f - Mathf.Clamp(forecast.cloudCover, 0f, 1f);
			perceived += forecast.sunTemperature * skyTransmission;
		}
		if (perceived < coldT + windEffect) { return 0; }
		if (perceived >= hotT + windEffect) { return 2; }
		return 1;
	}

	static int ClassifyCloud(float cloud)
	{
		if (cloud >= CloudOvercastThreshold) { return 2; }
		if (cloud >= CloudCloudyThreshold) { return 1; }
		return 0;
	}

	static void CopyWeather(WeatherData src, WeatherData dst)
	{
		dst.cloudCover = src.cloudCover;
		dst.windSpeed = src.windSpeed;
		dst.airTemperature = src.airTemperature;
		dst.sunTemperature = src.sunTemperature;
		dst.humidity = src.humidity;
		dst.rainAmount = src.rainAmount;
		dst.dustAmount = src.dustAmount;
	}

	static float ComputeDayIconAlpha(float tod, float fadeInStart, float fadeInEnd, float fadeOutStart, float fadeOutEnd)
	{
		if (tod < fadeInStart) { return 0f; }
		if (tod < fadeInEnd) { return (tod - fadeInStart) / (fadeInEnd - fadeInStart); }
		if (tod < fadeOutStart) { return 1f; }
		if (tod < fadeOutEnd) { return 1f - (tod - fadeOutStart) / (fadeOutEnd - fadeOutStart); }
		return 0f;
	}

	static float ComputeNightIconAlpha(float tod, float fadeOutStart, float fadeOutEnd, float fadeInStart, float fadeInEnd)
	{
		if (tod < fadeOutStart) { return 1f; }
		if (tod < fadeOutEnd) { return 1f - (tod - fadeOutStart) / (fadeOutEnd - fadeOutStart); }
		if (tod < fadeInStart) { return 0f; }
		if (tod < fadeInEnd) { return (tod - fadeInStart) / (fadeInEnd - fadeInStart); }
		return 1f;
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
