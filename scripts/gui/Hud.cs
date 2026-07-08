using Godot;
using System.Collections.Generic;

public partial class Hud : Control
{
	// Single active HUD per running game. Set in _Ready / cleared in
	// _ExitTree so callers outside the scene tree (LightningStrike's
	// proximity screen flash, etc.) can reach it without an explicit
	// reference chain.
	public static Hud Current { get; private set; }

	[Export] public GameClient gameClient;
	// Scene used for the persistent strip above the health bar — full
	// StatusEffectHud with icon + stack count badge + per-instance progress bar.
	[Export] PackedScene _statusEffectHudScene;
	// Scene used for the transient over-the-player notification — bare icon
	// only, no count, no progress bar. See StatusEffectIcon for the animation.
	[Export] PackedScene _statusEffectIconScene;
	[Export] WeaponHud _weaponLeftHud;
	[Export] WeaponHud _weaponRightHud;
	[Export] WeaponHud _consumableHud;
	[Export] ButtonHint _weaponLeftButtonHint;
	[Export] ButtonHint _weaponRightButtonHint;
	[Export] ButtonHint _consumableButtonHint;
	[Export] ButtonHint _selectConsumableButtonHint;
	// Persistent strip parent — usually an HBoxContainer above the health bar.
	[Export] Control _statusEffectContainer;
	// Screen-space anchor for the transient over-the-player notification.
	// Positioned each frame from _player.HudAnchor projected to screen so the
	// icon floats above the character's head. Children (queued
	// StatusEffectIcons) inherit the position automatically.
	[Export] Control _statusEffectNotificationAnchor;
	[Export] HudEventLog _eventLog;
	[Export] ProgressBar _healthBar;
	// Layered UNDERNEATH _healthBar (placed earlier in the scene tree) with
	// a transparent background and value = (Health + DrainedHealth) / MaxHealth,
	// so the dark-red fill spans [0, Health + DrainedHealth] and the bright
	// health fill paints over [0, Health] on top — leaving only the
	// [Health, Health + DrainedHealth] tail visible as an apparent extension
	// past current HP. Player keeps the invariant
	// `Health + DrainedHealth <= MaxHealth` — Heal forgives any drain it
	// climbs into, and TickBloodDrain shrinks drain while growing health in
	// lockstep — so the visible tail shrinks cleanly as drain heals back.
	[Export] ProgressBar _drainedHealthBar;
	[Export] ProgressBar _armorBar;
	// Weapon block-armor guard pool drawn as a dark extension past the right
	// end of _armorBar — same additive-underlay trick as _drainedHealthBar:
	// layered UNDERNEATH _armorBar with a transparent background and
	// value = (Armor + blockArmor) / (MaxArmor + blockCapacity), so the bright
	// armor fill paints over [0, Armor] and only the [Armor, Armor + blockArmor]
	// guard segment shows past it. Tinted dark grey while dormant and dark blue
	// while charging (the only state in which block armor actually absorbs).
	[Export] ProgressBar _blockArmorBar;
	[Export] ProgressBar _staminaBar;
	[Export] HudSignpostPanel _signpostPanel;
	[Export] ConversationController _dialoguePanel;
	[Export] HudRegionBanner _regionBanner;
	[Export] TextureRect _minimapTexture;
	// Shared "?" icon drawn for Sensed (unidentified) map markers. Optional —
	// null falls back to a drawn "?" glyph. Identified markers use their own icon.
	[Export] Texture2D _unknownMarkerIcon;
	[Export(PropertyHint.Range, "8,64,1")] int _markerIconSize = 24;
	[Export] ButtonHint _buttonHintTurnLeft;
	[Export] ButtonHint _buttonHintTurnRight;
	[Export] ButtonHint _buttonHintIndoors;
	[Export] ButtonHint _buttonHintMap;
	[Export] TextureRect _weatherDay;
	[Export] TextureRect _weatherNight;
	[Export] Control _weatherContainer;
	[Export] Control _objectivesContainer;
	[Export] PackedScene _objectivePanelScene;
	// Height above the player's feet to sample sunlight for the cave-fade.
	// The feet voxel straddles the solid ground, so a sub-voxel bob while
	// moving flips it between lit air and dark ground and flickers the icon;
	// sampling a voxel up reads stable open-air sunlight.
	[Export] float _weatherSunSampleHeight = 1f;
	// Seconds for the cave-fade to traverse its full 0..1 range. Fading out
	// (descending underground) is slow so brief shadow dips don't blink the
	// icon away; fading back in is quick so it reappears promptly in the open.
	[Export] float _weatherCaveFadeOutSeconds = 10f;
	[Export] float _weatherCaveFadeInSeconds = 1f;
	[Export] Control _itemWheel;
	[Export] Godot.Collections.Array<ItemSlotPanel> _itemSlots;
	// Full-screen white overlay flashed by TriggerLightningFlash. Sits
	// on top of every other HUD layer; mouse_filter = Ignore so it
	// never eats clicks. Alpha is driven each frame by _lightningFlashAlpha;
	// authored alpha in the scene should be 0 so the overlay is
	// invisible at boot.
	[Export] ColorRect _lightningFlashOverlay;
	// Current alpha of the lightning flash overlay (0 = invisible,
	// 1 = solid white). Decays at _lightningFlashFadeRate each second
	// back to 0.
	float _lightningFlashAlpha;
	float _lightningFlashFadeRate;
	Player _player;
	Inventory _inventory;
	// Consumable quick-select wheel. The four belt slots are laid out on a
	// compass in hud.tscn (slot 0 top, 1 right, 2 bottom, 3 left); the right
	// stick picks the nearest filled slot. Open state + current highlight are
	// driven from Player via ShowItemWheel / UpdateItemWheelHighlight /
	// CloseItemWheelAndGetSelection.
	bool _itemWheelOpen;
	int _itemWheelHighlight = -1;
	// Minimum stick deflection before the wheel re-evaluates which slot the
	// stick points at — below this the highlight holds steady.
	const float ItemWheelStickDeadzone = 0.4f;
	// Screen-space directions of the four belt slots, matching the compass
	// layout authored in hud.tscn (Y points down to match the right stick's
	// LookDown-positive axis).
	static readonly Vector2[] ItemWheelSlotDirections =
	{
		new Vector2(0f, -1f),  // slot 0 — top
		new Vector2(1f, 0f),   // slot 1 — right
		new Vector2(0f, 1f),   // slot 2 — bottom
		new Vector2(-1f, 0f),  // slot 3 — left
	};
	// Dim tint applied to the non-highlighted belt slots so the current pick
	// reads as the bright one.
	static readonly Color ItemWheelDimColor = new Color(0.5f, 0.5f, 0.5f, 0.7f);
	// FIFO of pending region banners. Chained region crossings can fire in a
	// row; we serialize them so each gets its full visible window. The
	// in-flight flag gates DispatchNext so the banner's onDone callback is the
	// only thing that advances the queue. Non-region announcements bypass this
	// entirely — they're pushed straight to the event log, which stacks lines.
	readonly Queue<Announcement> _announcementQueue = new();
	bool _announcementInFlight;
	// Persistent strip above the health bar — one StatusEffectHud entry per
	// distinct StatusEffectData with a stack-count badge and the per-instance
	// progress bar from the closest-to-expiry instance. Mirrors the original
	// pre-notification behavior.
	readonly Dictionary<StatusEffectData, StatusEffectHud> _statusEffectHuds = new();
	// One transient combat-objective panel per species the player is currently
	// fighting. Re-engaging a species refreshes its existing panel (and resets
	// its fade timer) rather than stacking a duplicate; each panel drops its own
	// entry here via its OnDismiss callback when it fades out and frees itself.
	readonly Dictionary<SpeciesData, ObjectivePanel> _objectivePanels = new();
	readonly Dictionary<StatusEffectData, int> _statusEffectCounts = new();
	// Smallest remaining-lifetime fraction [0,1] across the live instances of each
	// data — the instance closest to expiring, used to drive the strip's timer bar.
	readonly Dictionary<StatusEffectData, float> _statusEffectMinProgress = new();
	// Per-effect buildup meters in [0, 1+]. Refilled each tick from the
	// player's controller via FillStatusEffectBuildups. Entries here drive
	// (a) the buildup progress bar on each strip widget and (b) the
	// strip-entry visibility when an effect has buildup but no active stack
	// yet (e.g. partial poison from a gas cloud before the first stack lands).
	readonly Dictionary<StatusEffectData, float> _statusEffectBuildups = new();
	readonly List<StatusEffectData> _statusEffectsToRemove = new();
	// Transient over-the-player notification queue. Each newly-added effect data
	// pops a single icon that fades+shrinks in over 0.2s (3x → 1x), holds for
	// 1s, then fades out. Subsequent additions wait until the active icon
	// finishes so notifications don't overlap. `_seenStatusEffects` is the
	// per-tick diff baseline — anything in the player's current list that
	// wasn't there last tick is treated as new and enqueued.
	readonly HashSet<StatusEffectData> _seenStatusEffects = new();
	readonly HashSet<StatusEffectData> _statusEffectsThisTick = new();
	readonly Queue<StatusEffectData> _statusEffectQueue = new();
	StatusEffectIcon _activeStatusEffectIcon;
	float _mapRotation;
	// Marker icon overlay, lazily created as a child of _minimapTexture so it
	// shares the map's rect + camera-yaw rotation.
	MapMarkerOverlay _markerOverlay;
	// Lerped minimap view radius (meters), computed each frame from
	// TextureRect size and Minimap.pixelsPerMeter. Damps toward the target
	// so indoor/outdoor mode toggles glide smoothly.
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
	// dial. The 3×5 texture grid keys on temperature class (cold/normal/hot
	// against the player's authored thresholds) and weather class
	// (clear/cloudy/overcast/rainy/thunder). The weather-class axis is a
	// priority chain — thunder beats rainy beats cloud cover — so a
	// forecast with light rain AND an electric storm reads as thunder, not
	// rain.
	Texture2D[,] _weatherDayTextures;
	Texture2D[,] _weatherNightTextures;
	Texture2D _boundWeatherDayTexture;
	Texture2D _boundWeatherNightTexture;
	// Temporally smoothed cave-fade; eased toward the sampled target each
	// frame at asymmetric rates so the icon doesn't pop. Seeded to 1 (open).
	float _weatherCaveFade = 1f;
	// Working WeatherData / ZoneData reused each frame for the day/night
	// plateau forecast. Allocated lazily on first valid frame so HUD
	// construction order stays cheap.
	WeatherData _forecastEnvelope;
	WeatherData _forecastDayPeak;
	WeatherData _forecastNightTrough;
	ZoneData _forecastZone;
	// Cloud cover thresholds for the icon's three-stop cloud classification.
	// Authored deserts run near 0, overcast biomes push past 0.7 — splitting
	// the [0, 1] range into rough thirds reads as "few clouds / scattered /
	// blanketed" without flickering between classes on a quiet day.
	const float CloudCloudyThreshold = 0.33f;
	const float CloudOvercastThreshold = 0.66f;
	// Rain / lightning promotion thresholds. Both intentionally low —
	// they're "is the player perceiving rain / thunder right now" gates,
	// not "is this a heavy storm." simRain values around 0.1 already
	// produce audible drizzle (rain_light fades in from RainIntensity
	// 0.03 ≈ simRain 0.06) and faintly-visible particles, so the icon
	// should flip at the same point the player can hear / see it
	// rather than only when the storm is in full swing.
	const float RainyThreshold = 0.05f;
	const float ThunderThreshold = 0.05f;
	// Weather-class enum encoded as int index into the icon grid's
	// second axis. Kept as named consts rather than an enum to avoid
	// a cast at every Texture2D[,] lookup.
	const int WeatherClassClear = 0;
	const int WeatherClassCloudy = 1;
	const int WeatherClassOvercast = 2;
	const int WeatherClassRainy = 3;
	const int WeatherClassThunder = 4;
	const int WeatherClassCount = 5;
	// Cave fade: sunlight BFS at the player's voxel, smoothstepped to drive
	// the icons to zero alpha when the player descends below the sun's reach.
	// 0.25 of MAX_LIGHT is a soft threshold — a few voxels under an overhang
	// still shows weather; a proper cave drops it cleanly.
	const float CaveFadeSunlightFloor = 0.0f;
	const float CaveFadeSunlightFull = 0.25f;
	// Tints for the block-armor extension underlay. The fill stylebox is
	// white so these multiply straight through: dark grey while the guard is
	// dormant, dark blue while the weapon is charging and the pool is live.
	static readonly Color BlockArmorIdleColor = new Color(0.45f, 0.45f, 0.45f, 1f);
	static readonly Color BlockArmorChargingColor = new Color(0.15f, 0.3f, 0.7f, 1f);

	public override void _Ready()
	{
		Current = this;
		gameClient.onPlayerSpawned += OnPlayerSpawned;
		gameClient.onAnnouncement += OnAnnouncement;
		gameClient.onMobEngaged += OnMobEngaged;
		_signpostPanel.gameClient = gameClient;
		if (_dialoguePanel != null)
		{
			_dialoguePanel.gameClient = gameClient;
		}
		_weaponLeftButtonHint.SetHint("AttackContextSensitive", "AttackMelee", string.Empty, string.Empty);
		_weaponRightButtonHint.SetHint("AttackContextSensitive", "AttackRanged", "Aim", string.Empty);
		_consumableButtonHint.SetHint("UseItem", string.Empty);
		// Consumable quick-select: gamepad shows the wheel button
		// (ConsumableCycleRight); keyboard shows the direct hotbar key range.
		_selectConsumableButtonHint.SetHint("ConsumableCycleRight", string.Empty);
		_selectConsumableButtonHint.GlyphOverrideKeyboard = "1-4";
		_buttonHintTurnLeft.SetHint("CameraLeft", string.Empty);
		_buttonHintTurnRight.SetHint("CameraRight", string.Empty);
		_buttonHintIndoors.SetHint("CameraDown", string.Empty);
		_buttonHintMap.SetHint("Map", string.Empty);
		if (_signpostPanel != null)
		{
			_signpostPanel.Visible = false;
		}
		if (_itemWheel != null)
		{
			_itemWheel.Visible = false;
		}
		LoadWeatherTextures();
	}

	// 30 weather-icon textures keyed by phase × temp × weather class.
	// Loaded once here so per-frame icon swaps are dictionary-free index
	// lookups.
	void LoadWeatherTextures()
	{
		_weatherDayTextures = new Texture2D[3, WeatherClassCount];
		_weatherNightTextures = new Texture2D[3, WeatherClassCount];
		string[] tempLabels = { "cold", "normal", "hot" };
		string[] classLabels = { "clear", "cloudy", "overcast", "rainy", "thunder" };
		for (int t = 0; t < 3; t++)
		{
			for (int c = 0; c < WeatherClassCount; c++)
			{
				_weatherDayTextures[t, c] = GD.Load<Texture2D>(
					$"res://assets/textures/weather/day_{tempLabels[t]}_{classLabels[c]}.png");
				_weatherNightTextures[t, c] = GD.Load<Texture2D>(
					$"res://assets/textures/weather/night_{tempLabels[t]}_{classLabels[c]}.png");
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

	public bool IsConversationOpen => _dialoguePanel != null && _dialoguePanel.IsOpen;

	public void ShowConversation(ConversationData conversation, ConversationContext ctx, System.Action onClose = null)
	{
		_dialoguePanel?.Show(conversation, ctx, onClose);
	}

	public void CloseConversation()
	{
		_dialoguePanel?.Close();
	}

	public override void _ExitTree()
	{
		if (Current == this) { Current = null; }
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned -= OnPlayerSpawned;
			gameClient.onAnnouncement -= OnAnnouncement;
			gameClient.onMobEngaged -= OnMobEngaged;
		}
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventorySlotChanged;
			_inventory.onActiveConsumableChanged -= OnActiveConsumableChanged;
		}
	}

	// Per-frame decay of the lightning flash overlay's alpha. Runs
	// unconditionally (no player-alive gate) so the overlay still
	// fades out cleanly across a death / respawn boundary.
	void UpdateLightningFlash(float delta)
	{
		if (_lightningFlashOverlay == null) { return; }
		if (_lightningFlashAlpha > 0f && _lightningFlashFadeRate > 0f)
		{
			_lightningFlashAlpha -= _lightningFlashFadeRate * delta;
			if (_lightningFlashAlpha <= 0f)
			{
				_lightningFlashAlpha = 0f;
				_lightningFlashFadeRate = 0f;
			}
		}
		Color c = _lightningFlashOverlay.Color;
		if (Mathf.Abs(c.A - _lightningFlashAlpha) > 0.001f)
		{
			c.A = _lightningFlashAlpha;
			_lightningFlashOverlay.Color = c;
		}
	}

	// White full-screen flash for a lightning strike near the player.
	// `intensity` (0..1) is the peak overlay alpha (LightningStrike
	// computes this from the player's distance to the strike point).
	// `fadeSeconds` is how long the overlay takes to decay back to
	// transparent — short, so the flash reads as a stroke not a wash.
	// Multiple overlapping strikes take the MAX peak (a brighter
	// strike during a fading dim one shouldn't dim back down).
	public void TriggerLightningFlash(float intensity, float fadeSeconds)
	{
		if (intensity <= 0f) { return; }
		if (intensity > 1f) { intensity = 1f; }
		if (intensity > _lightningFlashAlpha) { _lightningFlashAlpha = intensity; }
		// Fade rate computed per strike from THIS strike's intensity
		// and fadeSeconds, not from the current alpha — keeps the
		// per-strike decay shape consistent regardless of overlap.
		if (fadeSeconds > 0f)
		{
			float rate = intensity / fadeSeconds;
			if (rate > _lightningFlashFadeRate) { _lightningFlashFadeRate = rate; }
		}
	}

	void OnAnnouncement(Announcement a)
	{
		if (a == null) { return; }
		// Region keeps its dedicated full-width banner, serialized through the
		// queue so chained crossings each get their full window. Everything else
		// is a fading line in the event log.
		if (a.type == EAnnouncementType.Region)
		{
			_announcementQueue.Enqueue(a);
			if (!_announcementInFlight)
			{
				DispatchNext();
			}
			return;
		}
		_eventLog?.Push(FormatEventLine(a));
	}

	// Compose a one-line event-log entry from an announcement's title/subtitle.
	// Title is bolded; a notice with no subtitle is just its title text.
	static string FormatEventLine(Announcement a)
	{
		string title = a.title ?? string.Empty;
		string subtitle = a.subtitle ?? string.Empty;
		if (string.IsNullOrEmpty(title))
		{
			return subtitle;
		}
		if (string.IsNullOrEmpty(subtitle))
		{
			return $"[b]{title}[/b]";
		}
		return $"[b]{title}[/b] {subtitle}";
	}

	void DispatchNext()
	{
		if (_announcementQueue.Count == 0)
		{
			_announcementInFlight = false;
			return;
		}
		Announcement next = _announcementQueue.Dequeue();
		_announcementInFlight = true;
		if (_regionBanner != null)
		{
			_regionBanner.Show(next.region, OnAnnouncementSurfaceDone);
		}
		else
		{
			OnAnnouncementSurfaceDone();
		}
	}

	void OnAnnouncementSurfaceDone()
	{
		// Defer the next dispatch — the banner that just finished is in the
		// middle of its tween-complete callback chain, and starting the next
		// presentation synchronously would re-enter Show on the same node
		// before its current state has settled.
		Callable.From(DispatchNext).CallDeferred();
	}

	// Player traded a blow with a mob (or just landed a credited kill, refreshed
	// from GameClient after the count is recorded) — show / refresh that species'
	// objective panel.
	void OnMobEngaged(SpeciesData species)
	{
		ShowObjective(species);
	}

	// Instantiate or refresh the combat-objective panel for `species`, binding it
	// to the species' current bestiary-level kill progress. Only species that
	// appear in the bestiary AND carry kill-level thresholds get a panel — there's
	// no experience progress to show otherwise.
	void ShowObjective(SpeciesData species)
	{
		if (species?.mob == null || !species.mob.appearsInBestiary
			|| _objectivesContainer == null || _objectivePanelScene == null)
		{
			return;
		}
		Godot.Collections.Array<int> thresholds = species.mob.killsPerLevel;
		if (thresholds == null || thresholds.Count == 0)
		{
			return;
		}

		WorldSimState sim = gameClient?.World?.WorldState?.SimState;
		int kills = sim != null && sim.TryGetBestiaryEntry(species, out MobBestiaryEntry entry)
			? entry.Kills : 0;

		int level = MobBestiaryEntry.ComputeLevel(kills, thresholds);
		bool atMax = level >= thresholds.Count;
		float fraction;
		string countText;
		if (atMax)
		{
			fraction = 1f;
			countText = Loc.Get(Loc.Keys.objective_max_level);
		}
		else
		{
			// Bar spans the current level's range only, matching the bestiary
			// panel: fills 0 → 1 between the previous threshold and the next.
			int prevThreshold = level > 0 ? thresholds[level - 1] : 0;
			int nextThreshold = thresholds[level];
			int span = Mathf.Max(1, nextThreshold - prevThreshold);
			fraction = (float)(kills - prevThreshold) / span;
			countText = $"{kills}/{nextThreshold}";
		}

		if (!_objectivePanels.TryGetValue(species, out ObjectivePanel panel)
			|| !GodotObject.IsInstanceValid(panel))
		{
			panel = _objectivePanelScene.Instantiate<ObjectivePanel>();
			_objectivesContainer.AddChild(panel);
			SpeciesData key = species;
			panel.OnDismiss = () => _objectivePanels.Remove(key);
			_objectivePanels[species] = panel;
		}
		panel.Set(GameClient.SpeciesDisplayName(species), fraction, countText);
	}

	// Rebind the HUD to a different party member when control switches (camp
	// Select-Character). Drops the old member's inventory subscriptions first so
	// they don't leak, then binds the new member exactly as a fresh spawn would.
	public void RebindPlayer(Player player)
	{
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventorySlotChanged;
			_inventory.onActiveConsumableChanged -= OnActiveConsumableChanged;
		}
		OnPlayerSpawned(player);
	}

	void OnPlayerSpawned(Player player)
	{
		_player = player;
		_inventory = player.Inventory;
		_inventory.onSlotChanged += OnInventorySlotChanged;
		_inventory.onActiveConsumableChanged += OnActiveConsumableChanged;
		RefreshSlot(EInventorySlot.WeaponMelee);
		RefreshSlot(EInventorySlot.WeaponRanged);
		RefreshSlot(EInventorySlot.Equipment);
		// Seed the diff baseline so persistent effects already on the player
		// at spawn (saved game restore, scripted intro state) don't all fire
		// notifications on the first tick after spawn.
		_seenStatusEffects.Clear();
		IReadOnlyList<StatusEffectState> effects = _player.StatusEffects;
		for (int i = 0; i < effects.Count; i++)
		{
			StatusEffectData data = effects[i]?.data;
			if (data != null && data.icon != null)
			{
				_seenStatusEffects.Add(data);
			}
		}
	}

	void OnInventorySlotChanged(EInventorySlot slot)
	{
		RefreshSlot(slot);
	}

	void OnActiveConsumableChanged(int index)
	{
		RefreshSlot(EInventorySlot.Equipment);
	}

	void RefreshSlot(EInventorySlot slot)
	{
		ItemState item = _inventory?.GetEquipped(slot);
		switch (slot)
		{
			case EInventorySlot.WeaponMelee:
				_weaponLeftHud.SetItem(item);
				_weaponLeftButtonHint.Visible = item != null;
				break;
			case EInventorySlot.WeaponRanged:
				_weaponRightHud.SetItem(item);
				_weaponRightButtonHint.Visible = item != null;
				break;
			case EInventorySlot.Equipment:
				_consumableHud.SetItem(item);
				_consumableButtonHint.Visible = item != null;
				break;
		}
	}

	// Open the consumable wheel: fill each belt slot from the player's
	// consumable hotbar, hide empty slots, and seed the highlight on the
	// currently-active consumable so a release with no stick input keeps it.
	public void ShowItemWheel()
	{
		if (_itemWheel == null || _itemSlots == null || _inventory == null)
		{
			return;
		}
		IReadOnlyList<ItemState> slots = _inventory.ConsumableSlots;
		for (int i = 0; i < _itemSlots.Count; i++)
		{
			ItemSlotPanel panel = _itemSlots[i];
			if (panel == null)
			{
				continue;
			}
			ItemState item = i < slots.Count ? slots[i] : null;
			panel.SetItem(item);
			panel.Visible = item != null;
		}
		_itemWheelOpen = true;
		_itemWheel.Visible = true;
		_itemWheelHighlight = IsSlotFilled(_inventory.ActiveConsumableIndex)
			? _inventory.ActiveConsumableIndex
			: FirstFilledSlot();
		ApplyItemWheelHighlight();
	}

	// Re-point the highlight at the filled slot whose compass direction best
	// matches the right-stick deflection. A centered stick (below the
	// deadzone) leaves the current highlight untouched.
	public void UpdateItemWheelHighlight(Vector2 stick)
	{
		if (!_itemWheelOpen)
		{
			return;
		}
		if (stick.LengthSquared() < ItemWheelStickDeadzone * ItemWheelStickDeadzone)
		{
			return;
		}
		Vector2 dir = stick.Normalized();
		int best = -1;
		float bestDot = -2f;
		int count = Mathf.Min(_itemSlots.Count, ItemWheelSlotDirections.Length);
		for (int i = 0; i < count; i++)
		{
			if (!IsSlotFilled(i))
			{
				continue;
			}
			float dot = dir.Dot(ItemWheelSlotDirections[i]);
			if (dot > bestDot)
			{
				bestDot = dot;
				best = i;
			}
		}
		if (best >= 0 && best != _itemWheelHighlight)
		{
			_itemWheelHighlight = best;
			ApplyItemWheelHighlight();
		}
	}

	// Hide the wheel and report the highlighted consumable slot index (or -1
	// if nothing was highlighted). Player uses the return value to select.
	public int CloseItemWheelAndGetSelection()
	{
		int selected = _itemWheelHighlight;
		_itemWheelOpen = false;
		_itemWheelHighlight = -1;
		if (_itemWheel != null)
		{
			_itemWheel.Visible = false;
		}
		return selected;
	}

	// A belt slot is "filled" iff it exists and holds an item — empty slots
	// are hidden and skipped by both highlighting and selection.
	bool IsSlotFilled(int index)
	{
		return index >= 0 && index < _itemSlots.Count
			&& _itemSlots[index] != null && _itemSlots[index].Item != null;
	}

	int FirstFilledSlot()
	{
		for (int i = 0; i < _itemSlots.Count; i++)
		{
			if (IsSlotFilled(i))
			{
				return i;
			}
		}
		return -1;
	}

	// Emphasize the highlighted belt slot — full white on the pick, dimmed on
	// the rest — so the player sees which consumable a release will select.
	void ApplyItemWheelHighlight()
	{
		for (int i = 0; i < _itemSlots.Count; i++)
		{
			ItemSlotPanel panel = _itemSlots[i];
			if (panel == null || !panel.Visible)
			{
				continue;
			}
			panel.Modulate = i == _itemWheelHighlight ? Colors.White : ItemWheelDimColor;
		}
	}

	public override void _Process(double delta)
	{
		UpdateLightningFlash((float)delta);

		if (_player == null)
		{
			return;
		}

		float maxHealth = _player.MaxHealth;
		_healthBar.MinValue = 0;
		_healthBar.MaxValue = 1;
		_healthBar.Value = maxHealth > 0f ? _player.Health / maxHealth : 0f;

		if (_drainedHealthBar != null)
		{
			float drained = _player.DrainedHealth;
			_drainedHealthBar.MinValue = 0;
			_drainedHealthBar.MaxValue = 1;
			_drainedHealthBar.Visible = drained > 0f;
			_drainedHealthBar.Value = maxHealth > 0f ? (_player.Health + drained) / maxHealth : 0f;
		}

		float maxArmor = _player.MaxArmor;
		_armorBar.MinValue = 0;
		_armorBar.MaxValue = 1;
		_armorBar.Visible = maxArmor > 0f;
		_armorBar.Value = maxArmor > 0f ? _player.Armor / maxArmor : 0f;
		// Physically shrink the bar when maxArmor < 100 so a weak piece of
		// armor reads as a shorter bar (even when fully charged), without
		// disturbing the 0..1 fill ratio. Caps at the health bar's width
		// when maxArmor reaches 100 — heavier armor reads as "full HP
		// bar's worth of protection" rather than overflowing past it.
		if (maxArmor > 0f)
		{
			Vector2 size = _armorBar.CustomMinimumSize;
			size.X = _healthBar.CustomMinimumSize.X * Mathf.Min(maxArmor, 100f) / 100f;
			_armorBar.CustomMinimumSize = size;
		}

		UpdateBlockArmorExtension(maxArmor);

		float maxStamina = _player.MaxStamina;
		_staminaBar.MinValue = 0;
		_staminaBar.MaxValue = 1;
		_staminaBar.Visible = maxStamina > 0f;
		_staminaBar.Value = maxStamina > 0f ? _player.Stamina / maxStamina : 0f;

		ulong now = gameClient.World?.GameTimeMs ?? 0;
		_weaponLeftHud.Tick(now, IsSlotCharging(EInventorySlot.WeaponMelee));
		_weaponRightHud.Tick(now, IsSlotCharging(EInventorySlot.WeaponRanged));
		_consumableHud.Tick(now, IsSlotCharging(EInventorySlot.Equipment));

		UpdateStatusEffects(now);

		_weaponLeftButtonHint.SetProgress(GetChargeProgress(EInventorySlot.WeaponMelee, now));
		_weaponRightButtonHint.SetProgress(GetChargeProgress(EInventorySlot.WeaponRanged, now));
		_consumableButtonHint.SetProgress(GetChargeProgress(EInventorySlot.Equipment, now));

		if (gameClient.camera.RotationDegrees.Y != _mapRotation)
		{
			_mapRotation = gameClient.camera.RotationDegrees.Y;
			_minimapTexture.RotationDegrees = _mapRotation;
		}

		UpdateMinimap();
		UpdateWeatherWidget(delta);
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
	void UpdateWeatherWidget(double delta)
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
		float halfWidth = sim.varianceCrossfadeHalfWidth01;
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
			Mathf.FloorToInt(pos.X), Mathf.FloorToInt(pos.Y + _weatherSunSampleHeight), Mathf.FloorToInt(pos.Z));
		float sunMask = Mathf.Clamp((float)sunBfs / LightEngine.MAX_LIGHT, 0f, 1f);
		float caveFadeTarget = Mathf.SmoothStep(CaveFadeSunlightFloor, CaveFadeSunlightFull, sunMask);
		float fadeSeconds = caveFadeTarget < _weatherCaveFade
			? _weatherCaveFadeOutSeconds : _weatherCaveFadeInSeconds;
		float caveFadeStep = fadeSeconds > 0f ? (float)delta / fadeSeconds : 1f;
		_weatherCaveFade = Mathf.MoveToward(_weatherCaveFade, caveFadeTarget, caveFadeStep);

		_weatherDay.Modulate = new Color(1f, 1f, 1f, dayAlpha * _weatherCaveFade);
		_weatherNight.Modulate = new Color(1f, 1f, 1f, nightAlpha * _weatherCaveFade);

		// Plateau-based forecast. Day icon shows the day plateau's weather,
		// night icon shows the night plateau's. Computed by re-blending
		// the zone envelope at the player's current XZ (so zone crossings
		// update the icons) and running WeatherSimulation.ApplyAtDiurnal
		// at diurnal=1 (day peak) and diurnal=0 (night trough). The icon
		// is steady across the diurnal ramps — it only updates when the
		// player's zone blend shifts or the variance handover settles.
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
		// shows the steady-state plateau, not a mid-handover lerp.
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
		float dayLightningVar = dayFadingOut ? ws.LightningVariancePrev
			: inDayPhase ? ws.LightningVarianceCur : ws.LightningVarianceNext;
		float nightWeatherVar = nightFadingOut ? ws.WeatherVariancePrev
			: inDayPhase ? ws.WeatherVarianceNext : ws.WeatherVarianceCur;
		float nightHumidityVar = nightFadingOut ? ws.HumidityVariancePrev
			: inDayPhase ? ws.HumidityVarianceNext : ws.HumidityVarianceCur;
		float nightCloudVar = nightFadingOut ? ws.CloudVariancePrev
			: inDayPhase ? ws.CloudVarianceNext : ws.CloudVarianceCur;
		float nightLightningVar = nightFadingOut ? ws.LightningVariancePrev
			: inDayPhase ? ws.LightningVarianceNext : ws.LightningVarianceCur;

		WeatherSimulation.ApplyAtDiurnal(_forecastDayPeak, _forecastZone, elevation, sim,
			diurnal: 1f, dayWeatherVar, 0f, dayHumidityVar, dayCloudVar, dayLightningVar);
		WeatherSimulation.ApplyAtDiurnal(_forecastNightTrough, _forecastZone, elevation, sim,
			diurnal: 0f, nightWeatherVar, 0f, nightHumidityVar, nightCloudVar, nightLightningVar);

		PlayerData pd = _player?.data;
		int dayTemp = ClassifyTemp(_forecastDayPeak, pd, includeSun: true);
		int dayClass = ClassifyWeather(_forecastDayPeak);
		int nightTemp = ClassifyTemp(_forecastNightTrough, pd, includeSun: false);
		int nightClass = ClassifyWeather(_forecastNightTrough);

		Texture2D dayTex = _weatherDayTextures[dayTemp, dayClass];
		Texture2D nightTex = _weatherNightTextures[nightTemp, nightClass];
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

	// Picks one slot from {clear, cloudy, overcast, rainy, thunder} for
	// the icon's weather-class axis. Priority order: thunder beats rainy
	// beats cloud cover, so a forecast with light rain AND an electric
	// storm reads as the more dramatic of the two — players plan around
	// the worst hazard present, not the average. Cloud classification
	// stays as the fallback for non-precipitating weather.
	static int ClassifyWeather(WeatherData forecast)
	{
		if (forecast.lightningAmount >= ThunderThreshold) { return WeatherClassThunder; }
		if (forecast.rainAmount >= RainyThreshold) { return WeatherClassRainy; }
		if (forecast.cloudCover >= CloudOvercastThreshold) { return WeatherClassOvercast; }
		if (forecast.cloudCover >= CloudCloudyThreshold) { return WeatherClassCloudy; }
		return WeatherClassClear;
	}

	static void CopyWeather(WeatherData src, WeatherData dst)
	{
		dst.cloudCover = src.cloudCover;
		dst.windSpeed = src.windSpeed;
		dst.airTemperature = src.airTemperature;
		dst.sunTemperature = src.sunTemperature;
		dst.humidity = src.humidity;
		dst.rainAmount = src.rainAmount;
		dst.lightningAmount = src.lightningAmount;
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
		float viewRadius = UpdateMinimapViewRadius(minimap);
		mat.SetShaderParameter("player_world_xz", new Vector2(pos.X, pos.Z));
		mat.SetShaderParameter("view_radius_meters", viewRadius);
		mat.SetShaderParameter("state_transition", minimap.StateTransition);

		// Marker overlay tracks the same framing the shader renders with. Parented
		// under the rotated TextureRect so it inherits the camera-yaw rotation.
		if (_markerOverlay == null)
		{
			// Minimap shows party ∪ active markers (the controlled player's field
			// discoveries appear immediately), matching its fog-of-war.
			_markerOverlay = MapMarkerOverlay.Create(gameClient, _unknownMarkerIcon, _markerIconSize, includeProvisional: true);
			_minimapTexture.AddChild(_markerOverlay);
		}
		_markerOverlay.SetFraming(new Vector2(pos.X, pos.Z), viewRadius);
	}

	// Computes the visible half-extent (meters) for the minimap shader.
	// Independent of player vision: the TextureRect's screen-pixel size
	// divided by Minimap.pixelsPerMeter gives the world meters
	// the rect covers; halve for the radius. Indoor mode multiplies
	// pixels-per-meter by Minimap.indoorZoom so corridors zoom in. Damp-
	// lerps toward the target so mode toggles glide smoothly.
	float UpdateMinimapViewRadius(Minimap minimap)
	{
		float ppm = minimap.pixelsPerMeter;
		if (minimap.Mode == Minimap.EMinimapMode.Indoor)
		{
			ppm *= minimap.indoorZoom;
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

	// Sync the persistent strip of status-effect icons against the player's
	// current list. Effects with the same data stack into one entry whose count
	// badge shows stack size; the progress bar tracks the timer of the instance
	// closest to expiry (or hides if every instance in the stack is persistent).
	// Also enqueues a transient over-the-player notification icon for every
	// newly-appearing data.
	void UpdateStatusEffects(ulong now)
	{
		_statusEffectCounts.Clear();
		_statusEffectMinProgress.Clear();
		_statusEffectsThisTick.Clear();

		double nowTod = World.Current?.TimeOfDayAbsolute ?? 0.0;
		IReadOnlyList<StatusEffectState> effects = _player.StatusEffects;
		for (int i = 0; i < effects.Count; i++)
		{
			StatusEffectState s = effects[i];
			if (s?.data == null || s.data.icon == null)
			{
				continue;
			}
			_statusEffectCounts.TryGetValue(s.data, out int prevCount);
			_statusEffectCounts[s.data] = prevCount + 1;
			if (s.IsTimed)
			{
				float progress = s.RemainingProgress(now, nowTod);
				if (!_statusEffectMinProgress.TryGetValue(s.data, out float prevProgress)
					|| progress < prevProgress)
				{
					_statusEffectMinProgress[s.data] = progress;
				}
			}
			// Over-head notification only fires on the active-effect edge —
			// buildups charging below the threshold don't pop the icon, only
			// the apply does. The diff is against last tick's *active* set
			// (_seenStatusEffects), so a buildup-only entry in the strip
			// doesn't poison the baseline.
			if (_statusEffectsThisTick.Add(s.data) && !_seenStatusEffects.Contains(s.data))
			{
				_statusEffectQueue.Enqueue(s.data);
			}
		}

		// Pull per-effect buildup meters; entries are folded into the strip
		// alongside active effects so an effect that only has buildup (no
		// stack yet) still gets a HUD slot with its buildup bar visible.
		_player.FillStatusEffectBuildups(_statusEffectBuildups);

		// Drop strip entries whose data no longer appears in either pool.
		_statusEffectsToRemove.Clear();
		foreach (var kv in _statusEffectHuds)
		{
			if (!_statusEffectCounts.ContainsKey(kv.Key) && !_statusEffectBuildups.ContainsKey(kv.Key))
			{
				kv.Value.QueueFree();
				_statusEffectsToRemove.Add(kv.Key);
			}
		}
		for (int i = 0; i < _statusEffectsToRemove.Count; i++)
		{
			_statusEffectHuds.Remove(_statusEffectsToRemove[i]);
		}

		// Build the union of "data to display" — active effects + any data
		// with a nonzero buildup meter. Reuse the counts dict as the union
		// driver by inserting buildup-only entries with count 0 (count
		// container is hidden below 2 so a 0-count entry reads as "just an
		// icon with a buildup bar").
		foreach (var kv in _statusEffectBuildups)
		{
			StatusEffectData data = kv.Key;
			if (data == null || data.icon == null)
			{
				continue;
			}
			if (!_statusEffectCounts.ContainsKey(data))
			{
				_statusEffectCounts[data] = 0;
			}
		}

		// Add / refresh strip entries for everything currently held.
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
			bool hasTimer = _statusEffectMinProgress.TryGetValue(data, out float progress);
			// Continuous-state effects (currently wet; future thirst / hunger /
			// cold / hot) can override the timer-based progress with a player-
			// side value via Player.GetStatusEffectProgress. When non-null we
			// also force the bar visible since these effects typically have
			// their timer paused (the underlying state controls arm/disarm
			// directly).
			float? customProgress = _player.GetStatusEffectProgress(data);
			if (customProgress.HasValue)
			{
				progress = customProgress.Value;
				hasTimer = true;
			}
			_statusEffectBuildups.TryGetValue(data, out float buildup);
			// ContinuousArm effects swap which bar they use based on whether
			// the controller has armed the status. Pre-arm: the buildup bar
			// fills as the meter rises toward armThreshold (matches
			// ThresholdCross effects). Post-arm: the standard progress bar
			// takes over showing the meter as the effect's intensity (0..1)
			// and the buildup bar hides — same visual transition the player
			// sees when a ThresholdCross effect's buildup crosses and becomes
			// an active state with a duration bar.
			if (data.buildupBehavior == EBuildupBehavior.ContinuousArm && count > 0)
			{
				progress = buildup;
				hasTimer = true;
				buildup = 0f;
			}
			hud.Set(data, count, progress, hasTimer, buildup);
		}

		_seenStatusEffects.Clear();
		foreach (StatusEffectData data in _statusEffectsThisTick)
		{
			_seenStatusEffects.Add(data);
		}

		UpdateStatusEffectNotification();
	}

	// Project the player's HudAnchor (or GlobalPosition fallback) to screen
	// each frame so the queued icon floats above the character's head, and
	// advance the queue: dispatch the next data when the active icon finishes,
	// otherwise let it run its 0.2s intro / 1s hold / 0.3s outro on its own
	// _Process. The anchor stays positioned even while empty so spawning a
	// fresh icon doesn't pop in from (0,0).
	void UpdateStatusEffectNotification()
	{
		if (_statusEffectNotificationAnchor == null)
		{
			return;
		}
		Vector3 worldPos = _player.hudAnchor != null ? _player.hudAnchor.GlobalPosition : _player.GlobalPosition;
		bool behindCamera = gameClient.camera.IsPositionBehind(worldPos);
		_statusEffectNotificationAnchor.Visible = !behindCamera;
		if (!behindCamera)
		{
			_statusEffectNotificationAnchor.Position = GameClient.Current.ProjectToScreen(worldPos);
		}

		if (_activeStatusEffectIcon != null && _activeStatusEffectIcon.IsFinished)
		{
			_activeStatusEffectIcon.QueueFree();
			_activeStatusEffectIcon = null;
		}
		if (_activeStatusEffectIcon == null && _statusEffectQueue.Count > 0 && _statusEffectIconScene != null)
		{
			StatusEffectData next = _statusEffectQueue.Dequeue();
			_activeStatusEffectIcon = _statusEffectIconScene.Instantiate<StatusEffectIcon>();
			_statusEffectNotificationAnchor.AddChild(_activeStatusEffectIcon);
			// Center the 40x40 icon on the anchor's origin (which sits at the
			// projected world point).
			_activeStatusEffectIcon.Position = new Vector2(-20f, -20f);
			_activeStatusEffectIcon.Init(next, autoOutro: true);
		}
	}

	// Whether the player is actively charging the weapon equipped in `slot`.
	// Drives the per-slot guard gauge's full-opacity vs. faint-ghost state —
	// the weapon's block armor is only active while that weapon is charging.
	bool IsSlotCharging(EInventorySlot slot)
	{
		if (_player?.Runner == null || !_player.Runner.IsBusy)
		{
			return false;
		}
		if (_player.Runner.Phase != EActionPhase.Charging)
		{
			return false;
		}
		return _player.Runner.Current.context.sourceSlot == slot;
	}

	// Drive the block-armor extension underlay. The pool shown is the weapon
	// currently being charged (the only weapon whose guard is live); when
	// nothing is charging we fall back to an equipped weapon's pool so the
	// reserve still reads as a dormant grey extension. Hidden entirely when no
	// equipped weapon carries block armor. Units are kept in lockstep with the
	// armor bar's units-per-pixel by sizing against the same MaxArmor=100 cap.
	void UpdateBlockArmorExtension(float maxArmor)
	{
		if (_blockArmorBar == null)
		{
			return;
		}
		WeaponState weapon = SelectBlockArmorWeapon(out bool charging);
		float capacity = weapon?.data?.blockArmor ?? 0f;
		float total = maxArmor + capacity;
		if (weapon == null || capacity <= 0f || total <= 0f)
		{
			_blockArmorBar.Visible = false;
			return;
		}
		_blockArmorBar.Visible = true;
		_blockArmorBar.MinValue = 0;
		_blockArmorBar.MaxValue = 1;
		_blockArmorBar.Value = (_player.Armor + weapon.blockArmor) / total;
		Vector2 size = _blockArmorBar.CustomMinimumSize;
		size.X = _healthBar.CustomMinimumSize.X * Mathf.Min(total, 100f) / 100f;
		_blockArmorBar.CustomMinimumSize = size;
		_blockArmorBar.Modulate = charging ? BlockArmorChargingColor : BlockArmorIdleColor;
	}

	// Picks the weapon whose block-armor pool the extension represents: the
	// charging weapon takes priority (its guard is the one actually absorbing),
	// otherwise the first equipped weapon that carries block armor so the
	// dormant reserve still shows. `charging` reports whether the returned
	// weapon is the one being charged, which drives the grey/blue tint.
	WeaponState SelectBlockArmorWeapon(out bool charging)
	{
		charging = false;
		WeaponState left = _inventory?.GetEquipped(EInventorySlot.WeaponMelee) as WeaponState;
		WeaponState right = _inventory?.GetEquipped(EInventorySlot.WeaponRanged) as WeaponState;
		bool leftHas = left?.data != null && left.data.blockArmor > 0f;
		bool rightHas = right?.data != null && right.data.blockArmor > 0f;
		if (leftHas && IsSlotCharging(EInventorySlot.WeaponMelee))
		{
			charging = true;
			return left;
		}
		if (rightHas && IsSlotCharging(EInventorySlot.WeaponRanged))
		{
			charging = true;
			return right;
		}
		if (leftHas)
		{
			return left;
		}
		if (rightHas)
		{
			return right;
		}
		return null;
	}

	// Charge fill across the current tier's hold window. With per-tier
	// chargeTime semantics, the bar fills 0 → 1 as `chargeT` ramps within
	// the selected tier, resets when the next tier takes over, then fills
	// again. A tier with chargeTime = 0 (snap fire) reports 1 immediately.
	// Cooldown is shown by WeaponHud, not here.
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
		ItemAction tier = action.selectedTier;
		if (tier == null || tier.chargeTime <= 0f)
		{
			return 1f;
		}
		float tierStart = ItemActionProfile.GetTierStartTime(profile, action.selectedTierIndex, tier.comboIndex);
		float elapsed = (nowMs - action.pressMs) / 1000f;
		return Mathf.Clamp((elapsed - tierStart) / tier.chargeTime, 0f, 1f);
	}
}
