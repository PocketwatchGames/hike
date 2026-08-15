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
	[Export] WeaponHud _lanternHud;
	[Export] ButtonHint _weaponLeftButtonHint;
	[Export] ButtonHint _weaponRightButtonHint;
	[Export] ButtonHint _consumableButtonHint;
	[Export] ButtonHint _lanternHint;
	[Export] Control _staminaContainer;
	[Export] PackedScene _staminaBarScene;
	// Persistent strip parent — usually an HBoxContainer above the health bar.
	[Export] Control _statusEffectContainer;
	// Screen-space anchor for the transient over-the-player notification.
	// Positioned each frame from _player.HudAnchor projected to screen so the
	// icon floats above the character's head. Children (queued
	// StatusEffectIcons) inherit the position automatically.
	[Export] Control _statusEffectNotificationAnchor;
	[Export] HudEventLog _eventLog;
	[Export] Control _questContainer;
	[Export] PackedScene _questItemScene;
	[Export] ProgressBar _healthBar;
	[Export] TextureRect _parryIcon;
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
	// The melee weapon's block-guard pool, drawn as its own row above the
	// health/armor row. Tinted dark grey while dormant, dark blue while
	// sneaking (the only state in which block armor actually absorbs), and
	// bright blue while the parry window is open.
	[Export] ProgressBar _blockArmorBar;
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
	// One pip per unit of max stamina (1 unit = 1 dash), instanced from
	// _staminaBarScene into _staminaContainer. Filled left to right, so the
	// recharging unit reads as a partial fill on the first non-full pip.
	readonly List<ProgressBar> _staminaBars = new();
	// Quest surfacing (view only — the quest lifecycle is sim-driven in World).
	// Bound to SimState.QuestLog on player spawn; one QuestItem widget per
	// active quest, refreshed each frame so counters / countdowns stay live.
	readonly Dictionary<QuestState, QuestItem> _questWidgets = new();
	QuestLog _questLog;
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
	// Lerped minimap view radius (meters) — the adaptive zoom. Follows the
	// player's current charting distance (Minimap.ComputeVisibleRevealRadiusMeters:
	// time-of-day light + night vision + vision stats). Damps toward the target so
	// day/night, gear, and mode changes glide instead of snapping.
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
	// Tints for the block-guard bar. The fill stylebox is white so these
	// multiply straight through: grey while the guard is dormant, saturated
	// blue while the player is sneaking and the pool is live. The parry window
	// is signalled separately by _parryIcon, not by a bar tint.
	static readonly Color BlockArmorIdleColor = new Color(0.45f, 0.45f, 0.45f, 1f);
	static readonly Color BlockArmorActiveColor = new Color(0.2f, 0.5f, 1f, 1f);

	// Parry icon states. Hidden while no parry can land (weapon can't parry, or
	// the guard is on its recharge cooldown); a dim grey while a parry is
	// available; a brighter near-white while the parry window is actually open,
	// so the window's opening reads as a clear "now" flash. The icon's texture is
	// light, so these modulate colors carry both the grey/white and the alpha.
	[Export] Color _parryAvailableColor = new Color(0.6f, 0.6f, 0.6f, 0.5f);
	[Export] Color _parryActiveColor = new Color(1f, 1f, 1f, 0.85f);

	// Horizontal scale of the vitals bars: pixels of bar width per point of
	// the stat, so a bigger max reads as a physically longer bar (a health
	// buff or an armor equip visibly grows it). Health and armor share the
	// same 1000-points-per-full-bar calibration (PlayerData.maxHealth defaults
	// to 1000, the same damage units armor absorbs in), but stay separately
	// tunable since armor chips at (1 + blunt) per damage point. Stamina needs
	// no scale here: its pip row is already one pip per unit.
	// Defaults: 1000 points = 250px.
	[Export(PropertyHint.Range, "0.01,5,0.01")] float _pixelsPerHealthPoint = 0.25f;
	[Export(PropertyHint.Range, "0.01,5,0.01")] float _pixelsPerArmorPoint = 0.25f;

	public override void _Ready()
	{
		Current = this;
		gameClient.onPlayerSpawned += OnPlayerSpawned;
		gameClient.onAnnouncement += OnAnnouncement;
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
		_lanternHint.SetHint("Lantern", string.Empty);
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
		}
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventorySlotChanged;
			_inventory.onConsumableChanged -= OnConsumableChanged;
		}
		UnbindQuests();
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

	// Rebind the HUD to a different party member when control switches (camp
	// Select-Character). Drops the old member's inventory subscriptions first so
	// they don't leak, then binds the new member exactly as a fresh spawn would.
	public void RebindPlayer(Player player)
	{
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventorySlotChanged;
			_inventory.onConsumableChanged -= OnConsumableChanged;
		}
		OnPlayerSpawned(player);
	}

	void OnPlayerSpawned(Player player)
	{
		_player = player;
		_inventory = player.Inventory;
		_inventory.onSlotChanged += OnInventorySlotChanged;
		_inventory.onConsumableChanged += OnConsumableChanged;
		RefreshSlot(EInventorySlot.WeaponMelee);
		RefreshSlot(EInventorySlot.WeaponRanged);
		RefreshSlot(EInventorySlot.Equipment);
		RefreshSlot(EInventorySlot.Lantern);
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

		BindQuests();
	}

	// (Re)bind to the world's quest log and rebuild widgets. Called on spawn and
	// on every party-switch rebind — the log is world-scope (same instance across
	// members), so we rebuild from its current contents and re-subscribe. Handles
	// quests seeded before this bind (rebuilt from Quests) as well as later
	// add/remove.
	void BindQuests()
	{
		UnbindQuests();
		_questLog = gameClient?.Sim?.WorldState?.SimState?.QuestLog;
		if (_questLog == null)
		{
			return;
		}
		foreach (QuestState quest in _questLog.Quests)
		{
			AddQuestWidget(quest);
		}
		_questLog.onQuestAdded += AddQuestWidget;
		_questLog.onQuestRemoved += RemoveQuestWidget;
	}

	void UnbindQuests()
	{
		if (_questLog != null)
		{
			_questLog.onQuestAdded -= AddQuestWidget;
			_questLog.onQuestRemoved -= RemoveQuestWidget;
			_questLog = null;
		}
		foreach (QuestItem item in _questWidgets.Values)
		{
			item.QueueFree();
		}
		_questWidgets.Clear();
	}

	void AddQuestWidget(QuestState quest)
	{
		if (quest == null || _questContainer == null || _questItemScene == null
			|| _questWidgets.ContainsKey(quest))
		{
			return;
		}
		QuestItem item = _questItemScene.Instantiate<QuestItem>();
		_questContainer.AddChild(item);
		item.Bind(quest);
		_questWidgets[quest] = item;
	}

	void RemoveQuestWidget(QuestState quest)
	{
		if (quest != null && _questWidgets.TryGetValue(quest, out QuestItem item))
		{
			item.QueueFree();
			_questWidgets.Remove(quest);
		}
	}

	void OnInventorySlotChanged(EInventorySlot slot)
	{
		RefreshSlot(slot);
	}

	void OnConsumableChanged()
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
			case EInventorySlot.Lantern:
				_lanternHud.SetItem(item);
				_lanternHint.Visible = item != null;
				break;
		}
	}

	public override void _Process(double delta)
	{
		using var _prof = Profiler.Sample("Hud.Process");

		UpdateLightningFlash((float)delta);

		if (_player == null)
		{
			return;
		}

		float maxHealth = _player.MaxHealth;
		_healthBar.MinValue = 0;
		_healthBar.MaxValue = 1;
		_healthBar.Value = maxHealth > 0f ? _player.Health / maxHealth : 0f;
		// Bar length tracks the stat. The drained underlay spans the same
		// 0..MaxHealth range as the health bar, so it always shares its width.
		SetBarWidth(_healthBar, maxHealth * _pixelsPerHealthPoint);
		SetBarWidth(_drainedHealthBar, maxHealth * _pixelsPerHealthPoint);

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
		SetBarWidth(_armorBar, maxArmor * _pixelsPerArmorPoint);

		UpdateBlockArmorBar();
		UpdateParryIcon();

		UpdateStaminaPips();

		ulong now = gameClient.Sim?.GameTimeMs ?? 0;
		_weaponLeftHud.Tick(now, IsSlotCharging(EInventorySlot.WeaponMelee));
		_weaponRightHud.Tick(now, IsSlotCharging(EInventorySlot.WeaponRanged));
		// The attuned spell's "ammo" is the live castable-count from the party
		// reagent pool; push it as a count-override (negative clears it when nothing
		// is attuned, falling back to the normal counter for any other consumable).
		_consumableHud.SetCountOverride(_inventory?.AttunedSpell != null ? _player.GetSpellAmmo() : -1);
		_consumableHud.Tick(now, IsSlotCharging(EInventorySlot.Equipment));
		_lanternHud.Tick(now, false);

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

		// Keep quest rows current (live counters / countdowns). The sim owns
		// add/remove; here we just re-render each active quest's display.
		foreach (KeyValuePair<QuestState, QuestItem> kv in _questWidgets)
		{
			kv.Value.Refresh(kv.Key.GetDisplay(now));
		}
	}

	// Sets a bar's minimum width in pixels (stat × pixels-per-point). Works for
	// both layouts in play: the container-driven health/drained pair (min size
	// feeds the MarginContainer) and the free-floating armor bars (a Control's
	// size is clamped up to its minimum; their authored offsets are 0).
	static void SetBarWidth(ProgressBar bar, float width)
	{
		if (bar == null)
		{
			return;
		}
		Vector2 size = bar.CustomMinimumSize;
		if (!Mathf.IsEqualApprox(size.X, width))
		{
			size.X = width;
			bar.CustomMinimumSize = size;
		}
	}

	// Sync the pip row against MaxStamina (armor and status effects change it in
	// whole units) and fill pips left to right from current stamina — full pips
	// first, then the fractional remainder on the next pip. Negative stamina
	// (dash/wall-jump overdraw) just reads as an empty row.
	void UpdateStaminaPips()
	{
		if (_staminaContainer == null || _staminaBarScene == null)
		{
			return;
		}
		int units = Mathf.Max(0, Mathf.RoundToInt(_player.MaxStamina));
		while (_staminaBars.Count < units)
		{
			ProgressBar bar = _staminaBarScene.Instantiate<ProgressBar>();
			_staminaContainer.AddChild(bar);
			_staminaBars.Add(bar);
		}
		while (_staminaBars.Count > units)
		{
			ProgressBar bar = _staminaBars[^1];
			_staminaBars.RemoveAt(_staminaBars.Count - 1);
			bar.QueueFree();
		}
		_staminaContainer.Visible = units > 0;
		float stamina = _player.Stamina;
		for (int i = 0; i < _staminaBars.Count; i++)
		{
			_staminaBars[i].Value = Mathf.Clamp(stamina - i, 0f, 1f);
		}
	}

	// Drive the clock-face weather widget: container rotation, the day→night icon
	// crossfade at sunset, icon selection from the pre-rolled day / night weather
	// slots, and an alpha-fade when the player is buried far enough underground
	// that no sunlight reaches their voxel.
	//
	// Rotation: 135° at sunrise (tod 0) → -135° at midnight (tod 0.75) → -225° at
	//   the day's end (tod 1) — one full turn, 15°/hour.

	// Day icon alpha:  fades in [0, 2·halfWidth], holds 1 to sunsetStart, fades
	//   out [sunsetStart, sunsetEnd], then 0 through the night.
	// Night icon alpha: 0 until sunsetStart, ramps 0→1 across the sunset window,
	//   holds 1 to the day's end. At a fresh sunrise both are ~0, so the previous day's
	//   night icon is gone rather than lingering into the new day.
	void UpdateWeatherWidget(double delta)
	{
		if (_weatherContainer == null || _weatherDay == null || _weatherNight == null)
		{
			return;
		}
		WorldState ws = gameClient?.Sim?.WorldState;
		SimData simData = ws?.SimData;
		if (ws == null || simData == null)
		{
			return;
		}

		float tod = (float)ws.TimeOfDay01;
		float halfWidth = simData.varianceCrossfadeHalfWidth01;
		float sunsetStart = (float)WorldState.SunsetTimeOfDay01 - halfWidth;
		float sunsetEnd = (float)WorldState.SunsetTimeOfDay01 + halfWidth;
		float sunriseFadeEnd = 2f * halfWidth;

		// Celestial dial: sweep the icons with the actual sun. The day runs
		// sunrise (tod 0) → the next sunrise (tod 1) and the orbit spans a full
		// turn over it, so the container rotates 135° → -225° (still 15°/hour,
		// and still -135° at midnight).
		_weatherContainer.RotationDegrees = 135f - 360f * tod;
		// Day icon fades in from sunrise and out at sunset; the night icon is
		// absent until sunset, then fades in and holds through midnight. At a fresh
		// sunrise (tod ≈ 0) both start at ~0 — the previous day's night icon is
		// already gone, and the new day's icon fades up from nothing.
		float dayAlpha = ComputeDayIconAlpha(tod, 0f, sunriseFadeEnd, sunsetStart, sunsetEnd);
		float nightAlpha = Mathf.Clamp((tod - sunsetStart) / Mathf.Max(sunsetEnd - sunsetStart, 1e-4f), 0f, 1f);

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

		// Forecast source per icon: the day icon shows the pre-rolled DAY weather
		// slot at the day plateau, the night icon the NIGHT slot at the night
		// trough. Both slots are rolled at sunrise (WorldState.RollDailyWeather),
		// so the whole day — and tonight — is known up front with no phase
		// bookkeeping. Slope 0: the icon shows the steady-state plateau.
		WeatherSimulation.ApplyAtDiurnal(_forecastDayPeak, _forecastZone, elevation, simData,
			diurnal: 1f, ws.DayWeatherVariance, 0f, ws.DayHumidityVariance, ws.DayCloudVariance, ws.DayLightningVariance);
		WeatherSimulation.ApplyAtDiurnal(_forecastNightTrough, _forecastZone, elevation, simData,
			diurnal: 0f, ws.NightWeatherVariance, 0f, ws.NightHumidityVariance, ws.NightCloudVariance, ws.NightLightningVariance);

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

	// Pushes the minimap's two-state crossfade snapshot into the shader
	// material each frame. State A is the previous mode/slice (decaying);
	// state B is the live one. `state_transition` lerps 0 → 1 so the shader
	// can mix(render(A), render(B), t) for a smooth fade across mode toggles
	// and slice crossings.
	void UpdateMinimap()
	{
		Minimap minimap = gameClient.Sim?.Minimap;
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
			// circleMaskFraction 0.5 matches the map shader's mask_radius so icons
			// clip to the round minimap; the world map (square) passes 0.
			_markerOverlay = MapMarkerOverlay.Create(gameClient, _unknownMarkerIcon, _markerIconSize, includeProvisional: true, circleMaskFraction: 0.5f);
			_minimapTexture.AddChild(_markerOverlay);
		}
		_markerOverlay.SetFraming(new Vector2(pos.X, pos.Z), viewRadius);
	}

	// Computes the visible half-extent (meters) for the minimap shader — the
	// adaptive zoom. Target = the player's current charting distance
	// (Minimap.ComputeVisibleRevealRadiusMeters, dimmed by time-of-day light +
	// night vision and scaled by vision stats) × viewRevealMargin, so the view
	// sits just inside what's charted and zooms in as night falls / out with a
	// vision buff. The margined view radius is then floored at minViewRadiusMeters
	// (the true on-screen radius floor — so max zoom-in matches the screen edge)
	// before indoor mode divides by indoorZoom to let corridors read closer.
	// Damp-lerps toward the target so the transitions glide.
	float UpdateMinimapViewRadius(Minimap minimap)
	{
		float target = minimap.ComputeVisibleRevealRadiusMeters() * minimap.viewRevealMargin;
		target = Mathf.Max(target, minimap.minViewRadiusMeters);
		if (minimap.Mode == Minimap.EMinimapMode.Indoor && minimap.indoorZoom > 0f)
		{
			target /= minimap.indoorZoom;
		}
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

		double nowTod = Sim.Current?.TimeOfDayAbsolute ?? 0.0;
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
			if (s.ShowsCountdownBar)
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
	// Drives each WeaponHud's charge-gauge fill for its slot.
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

	// Drive the block-guard bar. The pool shown is the equipped melee weapon's
	// guard — live (blue) while the player sneaks, a dormant grey reserve
	// otherwise. Hidden entirely when the melee weapon carries no block armor.
	// Sized on the same pixels-per-armor-point scale as the armor bar so guard
	// and armor capacities read in the same visual units.
	void UpdateBlockArmorBar()
	{
		if (_blockArmorBar == null)
		{
			return;
		}
		WeaponState weapon = SelectBlockArmorWeapon(out bool active);
		float capacity = weapon?.data?.blockArmor ?? 0f;
		if (weapon == null || capacity <= 0f)
		{
			_blockArmorBar.Visible = false;
			return;
		}
		_blockArmorBar.Visible = true;
		_blockArmorBar.MinValue = 0;
		_blockArmorBar.MaxValue = 1;
		_blockArmorBar.Value = weapon.blockArmor / capacity;
		SetBarWidth(_blockArmorBar, capacity * _pixelsPerArmorPoint);
		_blockArmorBar.Modulate = active ? BlockArmorActiveColor : BlockArmorIdleColor;
	}

	// Drive the parry icon: hidden when no parry can land, dim grey while a parry
	// is available (guard ready, weapon can parry), brighter near-white while the
	// parry window is actually open. Replaces the old block-bar parry tint.
	void UpdateParryIcon()
	{
		if (_parryIcon == null)
		{
			return;
		}
		if (_player.IsParryWindowActive)
		{
			_parryIcon.Visible = true;
			_parryIcon.Modulate = _parryActiveColor;
		}
		else if (_player.IsParryReady)
		{
			_parryIcon.Visible = true;
			_parryIcon.Modulate = _parryAvailableColor;
		}
		else
		{
			_parryIcon.Visible = false;
		}
	}

	// Picks the weapon whose block-armor pool the extension represents: the
	// equipped melee weapon, the only slot whose guard is live. Its reserve
	// shows as a dormant grey extension while stood, tinting blue while the
	// guard is live. `active` reports whether the guard is currently absorbing
	// — i.e. the player is sneaking — which drives the grey/blue tint.
	WeaponState SelectBlockArmorWeapon(out bool active)
	{
		active = false;
		WeaponState melee = _inventory?.GetEquipped(EInventorySlot.WeaponMelee) as WeaponState;
		if (melee?.data == null || melee.data.blockArmor <= 0f)
		{
			return null;
		}
		active = _player != null && _player.IsSneaking;
		return melee;
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
