using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;

public partial class GameClient : Node3D
{
	public static GameClient Current { get; private set; }

	// UI display strings for the inventory's per-action / per-context stat
	// readouts. Centralized here so a future localization pass swaps them
	// in one place instead of chasing string literals through every panel.
	public readonly Dictionary<EStatName, string> statNames = new Dictionary<EStatName, string>
	{
		{ EStatName.Damage, "Damage" },
		{ EStatName.ArmorPenetration, "Armor Penetration" },
		{ EStatName.Blunt, "Blunt" },
		{ EStatName.Dizzy, "Dizzy" },
		{ EStatName.Knockback, "Knockback" },
		{ EStatName.BloodCost, "Blood Cost" },
		{ EStatName.StaminaCost, "Stamina Cost" },
		{ EStatName.Cooldown, "Cooldown" },
		{ EStatName.Range, "Range" },
		{ EStatName.Reach, "Reach" },
		{ EStatName.TargetRange, "Target Range" },
		{ EStatName.Dps, "DPS" },
		{ EStatName.Radius, "Radius" },
		{ EStatName.Duration, "Duration" },
		{ EStatName.Ammo, "Ammo" },
		{ EStatName.Charges, "Charges" },
		{ EStatName.Heal, "Healing" },
		{ EStatName.MoveSpeed, "Move Speed" },
		{ EStatName.MaxStamina, "Stamina" },
		{ EStatName.ColdResist, "Cold Resist" },
		{ EStatName.HeatResist, "Heat Resist" },
		{ EStatName.Health, "Health" },
		{ EStatName.Armor, "Armor" },
		{ EStatName.Camouflage, "Camouflage" },
		{ EStatName.Vision, "Vision" },
		{ EStatName.NightVision, "Night Vision" },
		{ EStatName.Hearing, "Hearing" },
		{ EStatName.Noise, "Noise" },
		{ EStatName.Scent, "Scent" },
		{ EStatName.Fire, "Fire" },
		{ EStatName.Magical, "Magical" },
		{ EStatName.Poison, "Poison" },
		{ EStatName.Electrical, "Electrical" },
		{ EStatName.Ranged, "Ranged" },
		{ EStatName.Melee, "Melee" },
		{ EStatName.OutgoingDamage, "Outgoing Damage" },
		{ EStatName.AnimSpeed, "Animation Speed" },
		{ EStatName.FootprintAlpha, "Footprint Alpha" },
		{ EStatName.FootprintDuration, "Footprint Duration" },
	};

	// Damage modifier trigger labels. Used as the header of the conditional
	// damage panels under each weapon action ("Crit" / "Dizzy" / "Backstab").
	public readonly Dictionary<EDamageTrigger, string> damageTriggerLabels = new Dictionary<EDamageTrigger, string>
	{
		{ EDamageTrigger.OnCrit, "Crit" },
		{ EDamageTrigger.OnDizzy, "Dizzy" },
		{ EDamageTrigger.OnBackstab, "Backstab" },
	};

	[Export] public GameCamera camera;
	// Debug free-fly camera (WASD + right-drag), gated by the `debugFlyCam`
	// CVar. GameClient ticks it in _Process and forwards mouse-motion in _Input.
	[Export] public FlyCamera flyCamera;
	[Export] public Hud hud;
	[Export] public AlmanacScreen almanacScreen;
	[Export] public CookingScreen cookingScreen;
	[Export] public MerchantScreen merchantScreen;
	[Export] public StashScreen stashScreen;
	[Export] public DeathScreen deathScreen;
	[Export] public SleepOverlay sleepOverlay;
	[Export] public UpgradeScreen upgradeScreen;
	[Export] public Node worldHUD;
	[Export] public SubViewport sceneViewport;
	// Scene WorldEnvironment (SceneViewport/InnerEnv). Its built-in DEPTH fog
	// (fog_depth_begin/end, black) darkens distant ground for the normal iso
	// view; the bird's-eye overlook recedes it along the eased lift so the
	// overview isn't blacked out past the ground-level fog wall.
	[Export] public WorldEnvironment sceneEnvironment;
	// Pixel-art upscale rig (SubViewport render → snapped → upscale composite).
	// GameClient drives its per-frame snap from _Process; ProjectToScreen
	// forwards to it. See ViewportRig.
	[Export] public ViewportRig viewportRig;
	[Export] public ShaderMaterial fogMaterial;
	[Export] public PackedScene interactHudScene;
	// Shared world-pickup scene. Every dropped or spawned item materializes
	// through this one scene with its sprite swapped to the item's
	// worldSprite on spawn. The Loot runtime decides per-player whether to
	// auto-pickup (walk over) or require interact based on inventory state.
	[Export] public PackedScene lootScene;
	// Per-type floating-text scenes. GameClient.OnHudTextRequested picks one
	// from EHudTextType — each scene bakes its own color / fade duration /
	// vertical movement on the HudText script so callers only pass position
	// and text.
	[ExportGroup("Hud Text")]
	[Export] public PackedScene hudTextInfoScene;
	[Export] public PackedScene hudTextDamageLightScene;
	[Export] public PackedScene hudTextDamageHeavyScene;
	[Export] public PackedScene hudTextCritScene;
	[Export] public PackedScene hudTextBackstabScene;
	[Export] public PackedScene hudTextHealLightScene;
	[Export] public PackedScene hudTextHealHeavyScene;
	[ExportGroup("")]
	[Export] public ShaderMaterial outlineMaterial;
	// Flat-sprite outline variant. Used when ApplyHighlight is wrapping a
	// FlatLitSprite — the upright outline shader's vertex math would build
	// a Y-aligned billboard outline that misses the flat geometry by 90°.
	[Export] public ShaderMaterial outlineFlatMaterial;
	// Full-screen post-process pass (vignette / motion blur / damage flash /
	// low-health overlay + heartbeat). GameClient ticks it in _Process and
	// forwards damage / death events; see ScreenEffectsController.
	[Export] public ScreenEffectsController screenEffects;
	[ExportGroup("Aim Cursor")]
	// Aim-cursor saturation radius (pixels). Larger = more mouse travel before
	// the virtual cursor reaches the edge of its disk — i.e. lower sensitivity.
	// For Directional aim this only affects how far the cursor must travel to
	// saturate (the aim direction is atan2, magnitude-independent); for
	// Positional aim it sets the mouse-to-ground sensitivity directly, since the
	// disk position maps straight onto the ground disk (deflection = pos/radius).
	[Export(PropertyHint.Range, "20,1200,1")] public float aimCursorRadiusPx = 600f;
	// Below this magnitude the accumulator is treated as "at rest" and the
	// player's aim direction is left alone. Stops sub-pixel jitter from
	// continuously re-aiming when the player is trying to hold steady.
	[Export(PropertyHint.Range, "0,50,0.5")] public float aimCursorDeadzonePx = 5f;

	[ExportGroup("Subsystems")]
	// Authored as embedded child scenes in game.tscn — their tuning lives on
	// the Minimap / HeatField nodes, not here. World references and initializes
	// them rather than creating them.
	[Export] public Minimap minimap;
	[Export] public HeatField heatField;

	[ExportGroup("Foliage Player Fade")]
	// Cutaway tube radius around the camera→player capsule axis. The
	// effective radius pushed to the shader lerps between 0 (no cutaway)
	// and this value based on whether the CPU probe
	// (World.IsFadeVolumeOccluded) finds any fade-eligible cluster on the
	// camera→player line. So the effect is fully off in open terrain — no
	// invisible always-on fade tube nipping at nearby foliage — and ramps
	// to this size when the player walks behind canopy. Same value gates
	// the probe's sensitivity (a cluster needs to fall within
	// `clusterRadius + this` of the segment to count as occluding), so
	// the cutaway only activates when something it would actually hide
	// is in range.
	[Export(PropertyHint.Range, "0.2,10,0.05")] public float foliagePlayerFadeRadius = 1.8f;
	// Meters of soft-edge dither ramp at the radius boundary. Smaller = the
	// fade reads as a hard alpha-cut; larger = a lazy gradient. The shader
	// also perturbs the boundary with world-space sin noise (~±0.6m
	// amplitude) so it reads as irregular before the soft edge applies.
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float foliagePlayerFadeSoftEdge = 0.5f;
	// Anisotropic ellipse aspect — multipliers on the cutaway radius along
	// world horizontal (XZ) and world vertical (Y). Default (1.6, 1.2)
	// reads as ~16:9 framing (slightly wider than tall) with a vertical
	// bump that gives jumping players headroom to clear cover before the
	// boundary cuts back to baseline. 1:1:1 = isotropic tube (the
	// pre-anisotropic shape).
	[Export(PropertyHint.Range, "0.25,4,0.05")] public float foliagePlayerFadeAspectHorizontal = 1.6f;
	[Export(PropertyHint.Range, "0.25,4,0.05")] public float foliagePlayerFadeAspectVertical = 1.2f;
	// Vertical offsets from the player root (CharacterBody3D origin sits
	// at the feet plane) defining the capsule endpoints the fade tests
	// against. Feet offset lifts off ground so a bush at the player's toes
	// doesn't punch a fade hole; head offset bounds the canopy band that
	// actually obscures the silhouette.
	[Export(PropertyHint.Range, "0,1,0.05")] public float foliagePlayerFeetOffsetY = 0.2f;
	[Export(PropertyHint.Range, "0.5,3,0.05")] public float foliagePlayerHeadOffsetY = 1.7f;
	// Squared-fade lerp time constants. Rise is the fade-IN to the active
	// (expanded) radius — kept brisk so cover opens up promptly when the
	// player rounds a tree. Fall is the fade-OUT toward the held minimum
	// when the player is no longer tightly obscured but cover is still
	// nearby — longer so a brief loss-of-occlusion (walking a single step
	// out) doesn't snap the cutaway shut and re-open a moment later.
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float foliagePlayerFadeActivationRiseSeconds = 0.15f;
	[Export(PropertyHint.Range, "0.05,4,0.05")] public float foliagePlayerFadeActivationFallSeconds = 0.5f;
	// Activation amount (0..1) held while the player is NOT tightly
	// obscured but the wider probe still finds fading foliage nearby. Acts
	// as a pre-armed minimum cutaway — small enough to be visually
	// invisible (~0.1 × full radius), big enough that the rise toward full
	// is instantaneous when the player re-enters cover. When the wider
	// probe also fails, activation lerps gracefully toward 0.
	[Export(PropertyHint.Range, "0,1,0.01")] public float foliagePlayerFadeMinimumAmount = 0.12f;
	// Multiplier on the tight probe radius for the WIDE probe. Wide
	// detection range = tight × this. Default 2.0 — a tree ~5–6m off the
	// segment still registers as "nearby cover" without burning probe cost
	// on the next chunk over. Set 1.0 to disable the hold-at-minimum
	// behavior entirely (cutaway snaps off the moment tight clears).
	[Export(PropertyHint.Range, "1,4,0.1")] public float foliagePlayerFadeWideProbeMultiplier = 2.0f;
	// Density scaling — when Tight, the activation target lerps from
	// `foliagePlayerFadeCountScaleMin` (single isolated tree) up to 1.0
	// (`foliagePlayerFadeCountScaleSaturate`+ trees nearby in the WIDER
	// probe area). One tree behind the player in a clearing only nibbles
	// a small cutaway; standing inside a thicket opens the full authored
	// radius. Counted in the wide-probe radius (not just trees directly
	// between camera and player) since dense forest around a tight
	// occluder still benefits from a wider see-through.
	[Export(PropertyHint.Range, "0.05,1,0.05")] public float foliagePlayerFadeCountScaleMin = 0.35f;
	[Export(PropertyHint.Range, "1,16,1")] public int foliagePlayerFadeCountScaleSaturate = 5;

	[ExportGroup("Camera Clip Growth")]
	// World-space radius at which the player-centered ceiling-cutaway disk
	// reaches its full extent (i.e. blend=1 fully covers the band out to
	// this distance). Sized so the disk comfortably exceeds the screen
	// radius from the player at the default iso camera distance — anything
	// past it falls in the "phase > 1" tail and is fully clipped from the
	// first frame of the blend regardless of where the player is. 32m
	// keeps the iris sweep mostly on-screen for the iso framing — bigger
	// values move the deceleration of the ease curve further off-screen
	// (so distant pixels finish dithering before the slow finish kicks
	// in); smaller values bring more of the visible sweep into the
	// decel phase. Pixels past the radius are clamped to the boundary
	// in the shader, so corner pixels don't pop at completion regardless.
	[Export(PropertyHint.Range, "4,64,1")] public float cameraClipGrowthMaxRadius = 32f;
	// Thickness of the dithering ring at the iris's leading edge,
	// expressed as a fraction of `cameraClipGrowthMaxRadius`. Default 0.2
	// reads as about 1/8 of the screen on the standard iso framing. The
	// ring sweeps from -softness through 1+softness as blend goes 0→1,
	// so at blend=0 the very edge of the disk is just touching the
	// player's pixel, and at blend=1 the ring has fully passed the
	// max_radius extent. Smaller values = sharper edge (closer to a
	// circular cookie cutter); larger values = wider gradient at any
	// instant. World-space sin noise still wobbles the edge so even a
	// very thin softness reads as irregular.
	[Export(PropertyHint.Range, "0.02,1,0.01")] public float cameraClipGrowthEdgeSoftness = 0.2f;
	// World-space scan range for the IsFadeVolumeOccluded probe — measured
	// from the camera→player midpoint. Just needs to comfortably exceed the
	// camera-to-player distance so any cluster on that line is checked; 8m
	// gives the iso rig headroom without trawling distant entities.
	[Export(PropertyHint.Range, "2,32,0.5")] public float foliagePlayerFadeProbeRange = 8f;

	public Action<Player> onPlayerSpawned;
	// Fired from OnPlayerDiedInternal (GameClient's own player.onDied bridge),
	// so subscribers get death reliably without racing the deferred player
	// spawn the way subscribing to the player directly would.
	public Action<Player> onPlayerDied;
	// Floating world-space text request. Type picks which HudText scene is
	// instantiated (color / fade timing / vertical drift are baked per scene).
	// The default subscriber in Init forwards to OnHudTextRequested; callers
	// typically use the higher-level onDamage / onHeal buses below, which
	// format the number and pick a damage / heal type, then route through
	// this event.
	public Action<Vector3, string, EHudTextType> onHudText;
	// Combat HUD buses. Player and Mob fire onDamage on every damaging hit
	// and onHeal on every restoring heal (excluding blood-regen, which pays
	// back a debt rather than restoring lost HP). Default subscribers in
	// Init format the number and route through onHudText with the matching
	// damage / heal scene. Per-frame (DoT) sources accumulate on the actor
	// and flush once per second so a 60-tick burn doesn't spam 60 numbers.
	public Action<Vector3, float, EHudTextType> onDamage;
	public Action<Vector3, float, EHudTextType> onHeal;
	// Branching NPC conversation. Fired by Mob.SpeakDialogue when a Talk
	// interaction completes; OnConversationRequested forwards to the HUD's
	// ConversationController which picks the entry branch, types its lines,
	// and handles ui_accept advance/skip + player-input suppression while
	// open.
	public Action<ConversationData, ConversationContext> onConversation;
	// Upgrade / boon pick. A consumable's ApplyStatusEffect event fires this
	// with the menu of effects the item can bestow and a callback that applies
	// the player's chosen one (e.g. the fairy corpse). The default subscriber
	// (wired in Init) opens the UpgradeScreen modal; routing through an Action
	// keeps the effect-data layer decoupled from the GUI, same as onConversation.
	public Action<List<BoonData>, Action<BoonData>> startUpgradeSelection;
	public Action<bool> onPauseToggled;
	public Action onQuitToMenu;

	// The named region the player is currently within, or null on unnamed /
	// border terrain. Border chunks (RegionIndex points at a Regions[] entry
	// whose Data is null) keep this sticky; clearing back to null on extended
	// border travel is silent so the next named region's entry pulses the
	// banner cleanly. Region entry is surfaced through the Announce bus in
	// UpdateRegion. Set + read only here.
	RegionData CurrentRegion;

	// Generic announcement bus. Anything that wants to surface a one-shot
	// notification (region entry, recipe / item / language discovery,
	// future level-up / boss intro) builds an Announcement and routes it
	// through Announce. The Hud subscribes, queues entries, and dispatches
	// each to the appropriate surface (region banner vs panel) so callers
	// don't have to know about the visual layer.
	public Action<Announcement> onAnnouncement;
	// Gate that drops announcements at the source. Used during spawn-time
	// knowledge seeding and (future) save-load rehydration so the banner
	// queue doesn't pop for every initially-known item, recipe, region,
	// or language. The downstream discovery events on WorldSimState /
	// Player still fire — only the visual announcement is suppressed.
	public bool SuppressAnnouncements;
	public void Announce(Announcement a)
	{
		if (a == null || SuppressAnnouncements) { return; }
		onAnnouncement?.Invoke(a);
	}

	// Fired the moment a mob's Die() runs, with the per-instance
	// DamagedByPlayer flag piped through so subscribers can decide whether
	// the player earned credit (bestiary kill count, future quest counters).
	// GameClient subscribes its own bestiary bridge in Init.
	public Action<MobData, bool> onMobKilled;
	public void NotifyMobKilled(MobData mob, bool damagedByPlayer)
	{
		if (mob == null) { return; }
		onMobKilled?.Invoke(mob, damagedByPlayer);
	}

	// Player combat state, aggregated by CombatTracker from per-mob reports:
	// combat is on while a dangerous, player-perceived enemy is in an attack
	// behavior, lingers combatExitGraceSeconds after the player runs away, and
	// ends immediately when the last perceived threat is killed. These edge
	// events are the seam for music and any other in-combat reaction.
	public CombatTracker Combat { get; private set; }
	public Action onCombatBegin;
	public Action onCombatEnd;
	// Fires alongside onCombatEnd when combat ends by killing the last threat
	// (not by running away). Drives the victory music sting + finisher slow-mo.
	public Action onCombatVictory;
	public bool InCombat => Combat?.InCombat ?? false;
	[Export(PropertyHint.Range, "0,30,0.5")] public float combatExitGraceSeconds = 5f;
	// Wall-clock seconds to hold the finisher slow-mo before auto-releasing
	// (combat victory has no respawn to release on, unlike the death cam).
	[Export(PropertyHint.Range, "0,3,0.05")] public float combatVictorySlowMoSeconds = 0.5f;

	// Region-entry hysteresis. Wiggling on a seam mustn't flicker the
	// banner; an intentional crossing should fire within a step or two;
	// a chain of border zones can't keep the player tagged with a region
	// they've walked far away from. UpdateRegion runs the state machine
	// each tick.
	[ExportGroup("Region Hysteresis")]
	[Export(PropertyHint.Range, "0,10,0.1")] public float regionDwellSeconds = 1.5f;
	[Export(PropertyHint.Range, "0,8,0.25")] public float regionEnterDistanceChunks = 1.0f;
	// A bit larger than ZoneBlend.BlendRadiusChunks (= 2) so the visible
	// cross-blend band is fully inside the sticky range.
	[Export(PropertyHint.Range, "0,8,0.25")] public float regionBorderTravelChunks = 3.0f;
	[ExportGroup("")]
	RegionData _pendingRegion;
	Vector3 _pendingRegionEnterPos;
	float _pendingRegionElapsed;
	Vector3 _currentRegionEnterPos;

	public bool paused { get; private set; } = false;
	// Single gate that any input-consuming modal (map, inventory, etc.)
	// flips when it opens and clears when it closes. Players sees this and
	// skips ProcessInput; _UnhandledInput sees it and drops gameplay input.
	// World.Tick keeps running regardless so the runner can still advance a
	// consumable-use action started from the inventory screen.
	//
	// Setting to false is *deferred to end of _Process* rather than applied
	// synchronously. A modal closing on a shared key (B = ui_cancel + Sneak,
	// A = ui_accept + Jump) MUST keep the gate up for the rest of the current
	// frame, because Player.ProcessInput polls IsActionJustPressed which keeps
	// reporting true for the rest of the frame even after the modal marks the
	// event handled. CallDeferred and the process_frame signal both fire
	// before _Process, so they clear too early — the end-of-_Process flush
	// (after the gate read) is the only safe point. Setting to true is
	// immediate and cancels any pending clear.
	bool _inputSuppressed = false;
	bool _inputSuppressClearPending = false;
	public bool InputSuppressed
	{
		get => _inputSuppressed;
		set
		{
			if (value)
			{
				_inputSuppressed = true;
				_inputSuppressClearPending = false;
			}
			else
			{
				_inputSuppressClearPending = true;
			}
		}
	}
	public Player Player => _player;
	public World World => _world;

	Player _player;
	World _world;
	// Accumulator for the once-per-second sun + canopy print gated by
	// CVars.debugSkyLight. Frame-rate independent; counts deltaTime in
	// _Process and snaps the line whenever it crosses one second.
	double _debugSkyLightAccum;
	// Where the player was first placed — reused for respawn so the camera
	// snap and player teleport always land at the same authored / world-file
	// spawn point. WorldState.Spawn is the same value today, but holding
	// our own copy keeps respawn intact if a future save-load path mutates
	// WorldState.Spawn for a different purpose.
	Vector3 _spawnPosition;
	Vector2 _mousePosition;
	Sprite3D _highlightOverlay;
	InteractHUD _interactHUD;

	// Per-frame entity-spawn budget for the loading-screen-opaque window.
	// World defaults to 8/frame for hitch-free in-game streaming; 64 burns
	// through the inner sphere in a fraction of a second since the player
	// can't see the frame hitches behind the overlay. Reset to the default
	// before the fade so post-fade pop-in stays smooth.
	[ExportGroup("Loading")]
	[Export(PropertyHint.Range, "1,256,1")] public int loadingEntitySpawnBurst = 64;

	[ExportGroup("")]
	// Bird's-eye overlook driver — lifts the camera off the player into a
	// zoomed-out overview. GameClient ticks it in _Process, forwards the
	// player's onBirdsEye event, and reads its foliage/blur state; see
	// BirdsEyeController.
	[Export] public BirdsEyeController birdsEye;

	// Cinematic slow-motion + zoom on player death. Triggered in
	// OnPlayerDiedInternal, released in RespawnPlayer; ticked in _Process.
	[Export] public SlowMotionController slowMotion;
	// Wall-clock deadline (Time.GetTicksMsec) to auto-release the finisher
	// slow-mo, or 0 when none is pending. Wall-clock so the slow-mo it sets
	// doesn't stretch its own hold. Cleared on death so the death cam (held
	// until respawn) isn't released early by a leftover victory timer.
	ulong _victorySlowMoReleaseMs;

	// Wall-clock stamp for the post-process pass. The screen effects are
	// presentation, so they run on real time — the slow-mo death cam's
	// Engine.TimeScale must not stretch flash decays or the death heartbeat
	// (which is synced to the death-screen fade). The sim still gets the scaled
	// _Process delta via World.Tick.
	ulong _screenFxLastRealMs;

	// World → screen-pixel projection for the HUD layers. Forwards to the
	// viewport rig, which owns the sub-texel offset that aligns it with the
	// upscaled render.
	public Vector2 ProjectToScreen(Vector3 worldPos)
	{
		return viewportRig?.ProjectToScreen(worldPos) ?? Vector2.Zero;
	}

	public override void _Ready()
	{
		Current = this;
		Input.MouseMode = Input.MouseModeEnum.Captured;
		_highlightOverlay = new Sprite3D();
		_highlightOverlay.Name = "HighlightOverlay";
		_highlightOverlay.MaterialOverride = outlineMaterial;
		_highlightOverlay.AlphaCut = SpriteBase3D.AlphaCutMode.Disabled;
		_highlightOverlay.Visible = false;
		sceneViewport.AddChild(_highlightOverlay);

		// Start every input-consuming modal hidden regardless of how the
		// authored .tscn left them, and clear InputSuppressed so the player
		// can drive the world on the first frame. Saves a step-on-rake when a
		// new modal lands without `visible = false` on its instance line.
		if (almanacScreen != null)
		{
			almanacScreen.Visible = false;
		}
		if (cookingScreen != null)
		{
			cookingScreen.Visible = false;
		}
		if (merchantScreen != null)
		{
			merchantScreen.Visible = false;
		}
		if (stashScreen != null)
		{
			stashScreen.Visible = false;
		}
		if (deathScreen != null)
		{
			deathScreen.Visible = false;
		}
		if (upgradeScreen != null)
		{
			upgradeScreen.Visible = false;
		}

		_inputSuppressed = false;
		_inputSuppressClearPending = false;

	}

	public async void Init(Vector3 playerPosition, PackedScene playerScene, PlayerSpawnData playerSpawnData, WorldState worldState, LoadingScreen loadingScreen = null)
	{
		_spawnPosition = playerPosition;
		onHudText += OnHudTextRequested;
		onDamage += OnDamageRequested;
		onHeal += OnHealRequested;
		onConversation += OnConversationRequested;
		startUpgradeSelection += OnStartUpgradeSelection;

		// The loading screen owned by Main is up and currently sitting on
		// the chunk-fill phase (~60%). We keep gameplay input suppressed
		// for the rest of the load and hand it back when the screen fades.
		InputSuppressed = true;

		var phaseSw = Stopwatch.StartNew();
		_world = new World();
		Combat = new CombatTracker(combatExitGraceSeconds);
		Combat.onCombatBegin = () => onCombatBegin?.Invoke();
		Combat.onCombatEnd = () => onCombatEnd?.Invoke();
		Combat.onCombatVictory = OnCombatVictory;
		_world.onMobSpawned += OnMobSpawned;
		_world.onMobRemoved += OnMobRemoved;
		_world.onDiscoverableSpawned += OnDiscoverableSpawned;
		sceneViewport.AddChild(_world);
		// World.Initialize is the chunk-mesh sphere fill — fully synchronous
		// today (~900 chunks). The bar can't tick during this; it stays
		// frozen at 0.6 → 0.75 across the single hitch. Threading the
		// chunk fill (see voxels/CLAUDE.md) would make this smooth.
		_world.Initialize(worldState, playerPosition, camera, fogMaterial, () => _player?.GlobalPosition ?? playerPosition);
		GD.Print($"[Load] Building world (chunk-mesh fill): {phaseSw.ElapsedMilliseconds}ms");
		phaseSw.Restart();
		loadingScreen?.SetProgress(0.75f, "Spawning...");

		// Bridge sim-side discovery events to the announcement bus. The
		// underlying SimState lives across save/load and will outlive any
		// individual GameClient if we ever support hot-swapping the client;
		// no unsubscribe needed today because GameClient and WorldState are
		// torn down together.
		WorldSimState sim = worldState?.SimState;
		if (sim != null)
		{
			sim.onItemIdentified += OnSimItemIdentified;
			sim.onRecipeDiscovered += OnSimRecipeDiscovered;
			sim.onMobDiscovered += OnSimMobDiscovered;
		}
		onMobKilled += OnMobKilled;

		while (!_world.IsSpawnChunkReady(playerPosition))
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
		GD.Print($"[Load] Spawn-ready wait: {phaseSw.ElapsedMilliseconds}ms");
		phaseSw.Restart();

		_player = playerScene.Instantiate<Player>();
		_player.onHighlightChanged += OnPlayerHighlightChanged;
		_player.onInteractChanged += OnPlayerInteractChanged;
		_player.onLanguageLearned += OnPlayerLanguageLearned;
		_player.onDied += OnPlayerDiedInternal;
		if (birdsEye != null) { _player.onBirdsEye += birdsEye.SetActive; }
		sceneViewport.AddChild(_player);
		// Suppress announcements during spawn-time knowledge application so
		// the starting health potion, known recipes, etc. don't pop banners
		// on the first frame. Player.Initialize walks
		// PlayerSpawnData.initialKnowledge under this gate; everything else
		// it does (inventory seeding, ability setup) doesn't touch the bus.
		SuppressAnnouncements = true;
		try
		{
			_player.Initialize(_world, playerSpawnData, playerPosition, Vector3.Zero);
		}
		finally
		{
			SuppressAnnouncements = false;
		}

		// Burst the per-frame spawn budget while the loading overlay is
		// opaque — the player can't see frame hitches, so we trade smooth
		// frames for fewer of them. Reset to the in-game default right
		// before HideWithFade so the outer-shell drain (enqueued by
		// ExpandToFullEntityRadius) pops in at the normal rate.
		_world.MaxEntitiesPerFrame = loadingEntitySpawnBurst;
		_world.SetPlayer(_player);

		// Capture the peak entity-spawn count immediately after SetPlayer.
		// The chunk-mesh sphere is already fully loaded above, so SetPlayer's
		// SyncEntitiesToDesired call enqueues every entity for every chunk
		// in the initial (reduced) radius in one synchronous pass. From this
		// point on, PendingEntitySpawnCount only decreases until the wait
		// loop exits.
		int peakEntitySpawnCount = _world.PendingEntitySpawnCount;

		// Hold the loading screen up until every chunk in the initial entity
		// radius has finished draining its entity-spawn queue. Without this
		// wait, the screen would fade to reveal an empty world and props
		// would pop in after the camera was already active. The outer shell
		// (between the initial and full radius) is allowed to pop in
		// post-fade — those chunks aren't enqueued until ExpandToFullEntityRadius
		// runs below.
		while (!_world.AreEntitySpawnsDrained())
		{
			if (loadingScreen != null && peakEntitySpawnCount > 0)
			{
				int remaining = _world.PendingEntitySpawnCount;
				float drained = (float)(peakEntitySpawnCount - remaining) / peakEntitySpawnCount;
				loadingScreen.SetProgress(0.75f + drained * 0.25f);
			}
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
		GD.Print($"[Load] Spawning ({peakEntitySpawnCount} entities, inner radius): {phaseSw.ElapsedMilliseconds}ms");
		loadingScreen?.SetProgress(1f);

		camera.Init(sceneViewport);
		camera.SetInitialPosition(_player.GlobalPosition);

		onPlayerSpawned?.Invoke(_player);

		// Hand the entity drain back to the steady in-game cadence and
		// enqueue the outer shell of chunks — those entities trickle in
		// over the next few seconds while the player is getting oriented.
		_world.MaxEntitiesPerFrame = World.DEFAULT_MAX_ENTITIES_PER_FRAME;
		_world.ExpandToFullEntityRadius();

		// Begin the loading screen fade. LoadingScreen owns the timer and
		// QueueFrees itself when the fade hits 0; we drop InputSuppressed
		// here so gameplay input picks up the instant the screen starts
		// fading rather than waiting for it to finish.
		if (loadingScreen?.LoadStopwatch != null)
		{
			GD.Print($"[Load] Total (to fade start): {loadingScreen.LoadStopwatch.ElapsedMilliseconds}ms");
		}
		loadingScreen?.HideWithFade();
		InputSuppressed = false;
	}

	void OnSimItemIdentified(ItemData data)
	{
		if (data == null) { return; }
		WorldSimState sim = _world?.WorldState?.SimState;
		string name = sim != null ? sim.GetItemDisplayName(data) : data.displayName.ToString();
		Announce(new Announcement
		{
			type = EAnnouncementType.ItemIdentified,
			title = "Item Identified",
			subtitle = name,
			icon = data.inventorySprite,
		});
	}

	void OnSimRecipeDiscovered(RecipeData recipe)
	{
		if (recipe == null) { return; }
		ItemData output = recipe.outputItem;
		WorldSimState sim = _world?.WorldState?.SimState;
		string name = output == null
			? string.Empty
			: (sim != null ? sim.GetItemDisplayName(output) : output.displayName.ToString());
		Announce(new Announcement
		{
			type = EAnnouncementType.Recipe,
			title = "Recipe Discovered",
			subtitle = name,
			icon = output?.inventorySprite,
		});
	}

	void OnMobKilled(MobData mob, bool damagedByPlayer)
	{
		if (!damagedByPlayer || mob == null) { return; }
		WorldSimState sim = _world?.WorldState?.SimState;
		if (sim == null) { return; }

		// Snapshot the entry's level before the kill is recorded so we can
		// announce on threshold-crossing edges. A first-kill entry hasn't
		// been created yet — TryGetValue leaves kills at 0, which maps to
		// level 0 in ComputeLevel.
		int prevKills = sim.DiscoveredMobs.TryGetValue(mob, out MobBestiaryEntry prev) ? prev.Kills : 0;
		sim.RecordMobKill(mob);
		int newKills = sim.DiscoveredMobs.TryGetValue(mob, out MobBestiaryEntry next) ? next.Kills : prevKills;

		int prevLevel = MobBestiaryEntry.ComputeLevel(prevKills, mob.killsPerLevel);
		int newLevel = MobBestiaryEntry.ComputeLevel(newKills, mob.killsPerLevel);
		if (newLevel > prevLevel)
		{
			Announce(new Announcement
			{
				type = EAnnouncementType.MobLevelUp,
				title = "Bestiary Level Up",
				subtitle = $"{mob.displayName} Level {newLevel}",
			});
		}
	}

	void OnSimMobDiscovered(MobData mob)
	{
		if (mob == null) { return; }
		Announce(new Announcement
		{
			type = EAnnouncementType.MobDiscovered,
			title = "Creature Discovered",
			subtitle = mob.displayName.ToString(),
		});
	}

	void OnPlayerLanguageLearned(LanguageData language, ELanguageComponents addedComponents)
	{
		if (language == null) { return; }
		string langName = language.displayName.ToString();
		string subtitle = FormatLanguageSubtitle(langName, addedComponents);
		Announce(new Announcement
		{
			type = EAnnouncementType.LanguageLearned,
			title = "Language Learned",
			subtitle = subtitle,
		});
	}

	// Single-bit grants describe the specific component ("Vyeshal Grammar");
	// All-bit and multi-bit grants collapse to the language name to avoid
	// long compound strings in a 3-second banner. Vocabulary slots use a
	// 1/2/3 suffix so the player can tell partial vocabulary unlocks apart.
	static string FormatLanguageSubtitle(string langName, ELanguageComponents added)
	{
		if (added == ELanguageComponents.All || added == ELanguageComponents.None)
		{
			return langName;
		}
		string component = added switch
		{
			ELanguageComponents.Grammar => "Grammar",
			ELanguageComponents.Numbers => "Numbers",
			ELanguageComponents.Vocabulary1 => "Vocabulary 1",
			ELanguageComponents.Vocabulary2 => "Vocabulary 2",
			ELanguageComponents.Vocabulary3 => "Vocabulary 3",
			_ => null,
		};
		return component != null ? $"{langName} {component}" : langName;
	}

	[ExportGroup("Detail Sprite Reaction")]
	// Push radius and bend strength for the detail-sprite shader's player
	// reaction. ~0.6m matches the player's foot footprint; 0.25m bend reads
	// as grass parting around the player's legs without snapping flat.
	[Export(PropertyHint.Range, "0,4,0.05")] public float detailPlayerRadius = 0.6f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float detailPlayerStrength = 0.25f;

	[ExportGroup("Eye Adaptation")]
	// Rendering half of dark-adaptation. The 0..1 STATE is owned by the player sim
	// (Player.EyeDilation); this node reads it each frame and drives the lit-shader
	// tone curve (eye_adaptation.gdshaderinc) via the eye_adaptation render global,
	// shaped by the curve params below. Master scale; 0 makes the shader curve an
	// exact no-op. Live-settable via the `eye_adaptation` CVar for A/B.
	[Export(PropertyHint.Range, "0,1,0.01")] public float eyeAdaptationStrength = 1.0f;
	// Lift multiplier at the darkest (light_est = 0) when fully dilated.
	[Export(PropertyHint.Range, "1,16,0.1")] public float eyeAdaptDarkGain = 10.0f;
	// Lift multiplier at/above the knee (bright). Brights still get this (>1), and
	// the tonemap blows them out from there. Keep below dark gain.
	[Export(PropertyHint.Range, "1,8,0.05")] public float eyeAdaptLightGain = 2.0f;
	// Local light level (shader light_est scale, ~0..2) at which the lift has
	// fallen from dark gain to light gain. Larger = the ramp spans a wider tonal
	// range, which is what keeps the lift seam-free (no mid-tone cutoff).
	[Export(PropertyHint.Range, "0.1,3,0.05")] public float eyeAdaptKnee = 1.5f;
	[ExportGroup("")]

	// Per-frame smoothing state for the foliage cutaway radius. 0 = at base
	// radius, 1 = at active (expanded) radius. Lerped toward 1 when the
	// World probe finds the player occluded, toward 0 otherwise. Held
	// outside the Push method so its state carries across frames.
	private float _foliageFadeActivationAmount;

	private void PushFoliageOcclusionGlobals(double deltaSeconds)
	{
		if (_player == null || camera == null)
		{
			return;
		}
		Vector3 cameraWorld = camera.GlobalPosition;
		Vector3 playerPos = _player.GlobalPosition;
		Vector3 feet = playerPos + new Vector3(0f, foliagePlayerFeetOffsetY, 0f);
		Vector3 head = playerPos + new Vector3(0f, foliagePlayerHeadOffsetY, 0f);

		// Probe gates on the AUTHORED (full) radius, inflated by the larger
		// aspect axis so the sphere fully encloses the oblong ellipse. The
		// shader's per-pixel test still draws the actual ellipse boundary,
		// so over-eager probe activation in the ellipse's narrow corners is
		// harmless — at worst the cutaway expands without there being
		// anything visible to fade, which costs nothing visually.
		float tightProbeRadius = foliagePlayerFadeRadius * Mathf.Max(foliagePlayerFadeAspectHorizontal, foliagePlayerFadeAspectVertical);
		float wideProbeRadius = tightProbeRadius * foliagePlayerFadeWideProbeMultiplier;

		if (birdsEye != null && birdsEye.IsLifting)
		{
			// Entering the bird's-eye overlook: the camera lifts overhead, so the
			// iso-angle camera→player fade tube must close. Skip the probe and
			// clamp the live activation down to the driver's lift-tracking ceiling
			// — (1-t)² while rising, 0 at the apex — so the dithered iris contracts
			// in lockstep with the lift. min() means it only ever shrinks here,
			// never re-widens mid-contraction.
			_foliageFadeActivationAmount = Mathf.Min(_foliageFadeActivationAmount, birdsEye.FoliageActivationCeiling);
		}
		else
		{
			// Normal play — and the FlyDown return, which falls here the instant
			// the player cancels the overlook so the iris re-arms (widens back)
			// from the live probe as the camera descends rather than waiting for
			// it to land.
			//
			// Three-state target — always lerped, never snapped. A hard snap to
			// 0 on the None transition leaves a single frame where foliage was
			// being dithered at high activation and then suddenly isn't,
			// reading as a pop-edge along whatever cards the cutaway was
			// cutting through. Letting the fall lerp run smoothly from the
			// live activation down to 0 keeps the transition graceful even
			// when the player walks straight out of dense cover.
			//   Tight → density-scaled (min..1) — one isolated tree gets a
			//           small cutaway, a thicket opens the full radius.
			//   Wide  → minimum (held while still inside the forest neighborhood).
			//   None  → 0     (no nearby cover — drift to off).
			World world = World.Current;
			int nearbyPropCount = 0;
			FadeProbeResult probeResult = world != null
				? world.FadeProbe.Probe(cameraWorld, feet, head, tightProbeRadius, wideProbeRadius, foliagePlayerFadeProbeRange, out nearbyPropCount)
				: FadeProbeResult.None;

			float target;
			if (probeResult == FadeProbeResult.Tight)
			{
				// Saturation point of 1 means a single nearby tree already hits
				// full radius — guard so the divide can't go negative.
				int saturate = Mathf.Max(foliagePlayerFadeCountScaleSaturate, 1);
				float countNorm = Mathf.Clamp((nearbyPropCount - 1) / (float)Mathf.Max(saturate - 1, 1), 0f, 1f);
				target = Mathf.Lerp(foliagePlayerFadeCountScaleMin, 1f, countNorm);
			}
			else if (probeResult == FadeProbeResult.Wide)
			{
				target = foliagePlayerFadeMinimumAmount;
			}
			else
			{
				target = 0f;
			}
			float timeConstant = target > _foliageFadeActivationAmount
				? foliagePlayerFadeActivationRiseSeconds
				: foliagePlayerFadeActivationFallSeconds;
			float blend = 1f - Mathf.Exp(-(float)deltaSeconds / Mathf.Max(timeConstant, 1e-3f));
			_foliageFadeActivationAmount = Mathf.Lerp(_foliageFadeActivationAmount, target, blend);
		}

		// Inactive endpoint is literal zero — the shader short-circuits the
		// whole capsule + noise test when foliage_player_fade_radius drops
		// below its threshold, so the effect is genuinely off (not just
		// "narrow") while the player is in open terrain.
		float effectiveRadius = foliagePlayerFadeRadius * _foliageFadeActivationAmount;

		RenderingServer.GlobalShaderParameterSet("foliage_camera_world", cameraWorld);
		RenderingServer.GlobalShaderParameterSet("foliage_player_feet_world", feet);
		RenderingServer.GlobalShaderParameterSet("foliage_player_head_world", head);
		RenderingServer.GlobalShaderParameterSet("foliage_player_fade_radius", effectiveRadius);
		RenderingServer.GlobalShaderParameterSet("foliage_player_fade_soft_edge", foliagePlayerFadeSoftEdge);
		RenderingServer.GlobalShaderParameterSet("foliage_player_fade_aspect", new Vector2(foliagePlayerFadeAspectHorizontal, foliagePlayerFadeAspectVertical));

		// Camera-clip growth disk — pinned to the live player position so
		// the iris of the ceiling cutaway tracks them through the
		// transition. clip_dither.gdshaderinc reads these to delay each
		// band pixel's transition by distance to the player, then noises
		// the boundary with the same sin signature the foliage cutaway
		// uses.
		RenderingServer.GlobalShaderParameterSet("camera_clip_growth_center", playerPos);
		RenderingServer.GlobalShaderParameterSet("camera_clip_growth_max_radius", cameraClipGrowthMaxRadius);
		RenderingServer.GlobalShaderParameterSet("camera_clip_growth_edge_softness", cameraClipGrowthEdgeSoftness);
	}

	public override void _Process(double deltaTime)
	{
		// Push the foliage player-occlusion fade globals before the pause /
		// console gates — even while paused the camera or player anchors
		// can still drift (mid-pause shake, debug-cam fly), and a stale fade
		// volume would visibly punch the wrong hole in the canopy.
		PushFoliageOcclusionGlobals(deltaTime);

		if (_player == null || ConsoleUI.IsOpen || paused)
		{
			return;
		}
		_world.Tick(deltaTime);
		Combat?.Tick(_world.GameTimeMs);
		UpdateRegion(deltaTime);
		UpdateDebugSkyLight(deltaTime);

		if (!InputSuppressed)
		{
			// Any modal that wants to block gameplay input flips
			// InputSuppressed in its Open(); World.Tick keeps running so a
			// consumable-use action started from the inventory screen can
			// still advance through the runner.
			_player.ProcessInput(camera.Yaw);
		}
		else
		{
			// Input suppressed by a modal. ClearInput zeroes the cached
			// move/look vectors so a stick held when the modal opened
			// doesn't keep coasting the character — _PhysicsProcess reads
			// _inputMove every frame regardless of who last wrote it.
			_player.ClearInput();
		}

		// Recenter the virtual aim cursor when not aiming so each new aim
		// session starts centered. Gated on IsAiming so a mid-charge release
		// of the stick (Positional aim with the cursor parked away from
		// center) doesn't get zeroed out from under the player — IsAiming
		// stays true through a charge even when the Aim button is released.
		// The _Input gate above blocks motion accumulation while not aiming;
		// this just clears any residue between sessions.
		if (_player != null && !_player.IsAiming)
		{
			_mousePosition = Vector2.Zero;
		}

		// Per-frame push to the detail_sprite shader so grass bends around
		// the player. Single global, sub-byte cost; written every frame so
		// stale values don't persist when the player teleports.
		RenderingServer.GlobalShaderParameterSet("player_pos", _player.GlobalPosition);
		RenderingServer.GlobalShaderParameterSet("player_radius", detailPlayerRadius);
		RenderingServer.GlobalShaderParameterSet("player_strength", detailPlayerStrength);

		// Eye adaptation: the player sim owns the dilation STATE; we read it and
		// drive the lit-shader tone curve. Globals are declared in project.godot,
		// so a plain Set (no Register) matches the player_pos pushes above.
		RenderingServer.GlobalShaderParameterSet("eye_adaptation", _player.EyeDilation * eyeAdaptationStrength);
		RenderingServer.GlobalShaderParameterSet("eye_adapt_dark_gain", eyeAdaptDarkGain);
		RenderingServer.GlobalShaderParameterSet("eye_adapt_light_gain", eyeAdaptLightGain);
		RenderingServer.GlobalShaderParameterSet("eye_adapt_knee", eyeAdaptKnee);

		if (birdsEye != null && birdsEye.IsActive)
		{
			birdsEye.UpdateCamera(deltaTime);
			viewportRig?.SnapAndUpscale();
			// Sprites are sized off `sprite_chunky` (world meters per inner-viewport
			// texel) — SnapCameraAndUpdateUpscale ties it to the live ortho Size so
			// the pixel-art look stays "1 source pixel = N screen pixels". During
			// the fly-up we WANT sprites to shrink with the zoom, so re-anchor the
			// uniform to the pre-zoom Size (ApplySpriteChunky). Snap math has
			// already run against the live (inflated) chunky, so the camera's grid
			// stays consistent; only the sprite scaler is reverted. Sub-pixel
			// sprite rendering during the overview is the explicit tradeoff for a
			// view that actually reads as zoomed out.
			birdsEye.ApplySpriteChunky();
			CullProps(camera.Clip);
		}
		else if (CVars.debugFlyCam.Value)
		{
			flyCamera?.Tick(deltaTime);
			CullProps(float.PositiveInfinity);
		}
		else
		{
			flyCamera?.Reset();
			float followTime;
			if (_player.IsDashing)
			{
				followTime = camera.followTimeDashing;
			}
			else if (!_player.IsGrounded && _player.Velocity.Y > 0f)
			{
				followTime = camera.followTimeAirAscending;
			}
			else if (_player.IsSprinting)
			{
				followTime = camera.followTimeSprinting;
			}
			else
			{
				followTime = camera.followTimeNormal;
			}
			camera.UpdateCamera(deltaTime, _player.GlobalPosition, followTime);
			// Auto-release the finisher slow-mo once its wall-clock hold elapses
			// (the death cam, by contrast, holds until respawn).
			if (_victorySlowMoReleaseMs != 0 && Time.GetTicksMsec() >= _victorySlowMoReleaseMs)
			{
				_victorySlowMoReleaseMs = 0;
				slowMotion?.Release();
				camera?.ClearFocus();
			}
			// Apply the slow-mo zoom override to camera.Size BEFORE the pixel-snap
			// reads it (the rig sizes its texel grid off the live ortho Size).
			slowMotion?.Update();
			viewportRig?.SnapAndUpscale();
			CullProps(camera.Clip);
		}
		// Sync the cap-mask camera AFTER the chunky-pixel snap so the mask
		// renders at the same snapped pose as the main scene. Mask
		// SubViewport size matches the inner pre-upscale size for 1:1
		// SCREEN_UV alignment.
		if (sceneViewport != null)
		{
			camera.SyncCapMaskCamera(sceneViewport.Size);
		}
		// Bird's-eye fly-up and the slow-mo death cam are both zooms → radial channel.
		float radialBlur = Mathf.Max(slowMotion?.RadialBlur ?? 0f, birdsEye?.MotionBlur ?? 0f);
		// Drive the post-process on wall-clock time (see _screenFxLastRealMs) so
		// slow-mo doesn't stretch its fades; the sim got the scaled delta above.
		ulong screenFxNowMs = Time.GetTicksMsec();
		double screenFxDelta = _screenFxLastRealMs == 0UL ? deltaTime : (screenFxNowMs - _screenFxLastRealMs) / 1000.0;
		_screenFxLastRealMs = screenFxNowMs;
		screenEffects?.Tick(screenFxDelta, radialBlur);

		// Hide the per-interactive highlight outline while another fullscreen
		// HUD (merchant, conversation, cooking, etc.) has InputSuppressed on.
		// The InteractHUD's own options modal also sets InputSuppressed but
		// should NOT hide the outline — exclude that case via ModalOpen.
		// Done here per-frame rather than in ApplyHighlight / RemoveHighlight
		// because external HUDs can open / close without the player's
		// highlight target changing.
		if (_highlightOverlay != null)
		{
			bool ownModalActive = _interactHUD != null && _interactHUD.ModalOpen;
			bool externalHudActive = InputSuppressed && !ownModalActive;
			// Only show the SPRITE overlay for sprite interactives. Mesh
			// interactives (statue/sign/chest/ladder) drive their own inverted-hull
			// outline via _meshHighlight; without this gate the overlay is forced
			// visible here still carrying the PREVIOUS sprite target's texture and
			// transform — the "stale villager highlight in a weird place" ghost.
			bool birdsEyeActive = _player?.IsBirdsEye ?? false;
			bool shouldShow = _player?.HighlightInteractive != null && !externalHudActive && _meshHighlight == null && !birdsEyeActive;
			if (_highlightOverlay.Visible != shouldShow)
			{
				_highlightOverlay.Visible = shouldShow;
			}
		}

		// Service the deferred input-suppress clear AFTER ProcessInput has
		// been gated for this frame. See InputSuppressed property docs.
		if (_inputSuppressClearPending)
		{
			_inputSuppressed = false;
			_inputSuppressClearPending = false;
		}
	}

	// Reads the region under the player and turns the raw "what region am
	// I in?" stream into a stable "what named region am I in?" signal.
	// Hysteresis rules:
	//   - Candidate region differs from CurrentRegion: dwell timer
	//     accumulates; commit the swap (and announce the region) once
	//     the player has stayed in the candidate's chunks for
	//     regionDwellSeconds or moved regionEnterDistanceChunks
	//     past where the dwell started.
	//   - Underfoot chunk is a border (Regions[i].Data == null):
	//     CurrentRegion stays put until the player has traveled
	//     regionBorderTravelChunks from where they entered, then
	//     CurrentRegion clears silently.
	void UpdateRegion(double deltaTime)
	{
		WorldState ws = _world?.WorldState;
		if (ws == null) { return; }

		Vector3 playerPos = _player.GlobalPosition;
		RegionData candidate = SampleRegion(playerPos, ws);

		if (candidate == null)
		{
			// Border zone (or unloaded chunk). Drop any pending swap —
			// we left the candidate's territory before dwelling.
			_pendingRegion = null;
			_pendingRegionElapsed = 0f;

			if (CurrentRegion != null)
			{
				if (ChunkDistanceXZ(playerPos, _currentRegionEnterPos) > regionBorderTravelChunks)
				{
					CurrentRegion = null;
				}
			}
			return;
		}

		if (candidate == CurrentRegion)
		{
			// Re-entered the current region after dipping into a
			// border. Cancel any pending swap and re-anchor the sticky
			// center so subsequent border travel measures from here.
			_pendingRegion = null;
			_pendingRegionElapsed = 0f;
			_currentRegionEnterPos = playerPos;
			return;
		}

		// Candidate is a different named region — run the dwell.
		if (candidate != _pendingRegion)
		{
			_pendingRegion = candidate;
			_pendingRegionEnterPos = playerPos;
			_pendingRegionElapsed = 0f;
		}
		else
		{
			_pendingRegionElapsed += (float)deltaTime;
		}

		bool dwellMet = _pendingRegionElapsed >= regionDwellSeconds;
		bool distMet = ChunkDistanceXZ(playerPos, _pendingRegionEnterPos) >= regionEnterDistanceChunks;
		if (dwellMet || distMet)
		{
			CurrentRegion = candidate;
			_currentRegionEnterPos = playerPos;
			_pendingRegion = null;
			_pendingRegionElapsed = 0f;
			ws.SimState.DiscoveredRegions.Add(CurrentRegion);
			Announce(new Announcement
			{
				type = EAnnouncementType.Region,
				region = CurrentRegion,
			});
		}
	}

	// Once-per-second console line summarizing the LightMap reading at the
	// player's voxel. Toggled by the debug_sky_light CVar; off by default.
	// Used to verify foliage canopy shadowing: with the CVar on, walk into
	// and out of a tree's footprint and watch sun01 drop below 0.7 (the
	// rain shader's threshold for hiding drops) and canopy go above 0.
	void UpdateDebugSkyLight(double deltaTime)
	{
		if (!CVars.debugSkyLight.Value)
		{
			_debugSkyLightAccum = 0;
			return;
		}
		_debugSkyLightAccum += deltaTime;
		if (_debugSkyLightAccum < 1.0)
		{
			return;
		}
		_debugSkyLightAccum = 0;

		WorldState ws = _world?.WorldState;
		if (ws == null || _player == null) { return; }
		Vector3 pos = _player.GlobalPosition;
		int wx = Mathf.FloorToInt(pos.X);
		int wy = Mathf.FloorToInt(pos.Y);
		int wz = Mathf.FloorToInt(pos.Z);
		int sun = ws.GetSunlightWorld(wx, wy, wz);
		float sun01 = ws.GetSkyLight01(pos);
		int canopy = ws.GetCanopyAttenuationWorld(wx, wy, wz);
		GD.Print($"[SkyLight] voxel=({wx},{wy},{wz}) sun={sun}/{LightEngine.MAX_LIGHT} sky01={sun01:F2} canopy={canopy}/255");
		// Walk the column upward from the player and dump (Y, sun, canopy)
		// so we can see whether canopy density is present at the cluster
		// altitude and whether ComputeSunlight attenuated through it.
		var col = new System.Text.StringBuilder();
		col.Append("[SkyLight column up]");
		for (int dy = 0; dy <= 14; dy++)
		{
			int yy = wy + dy;
			int s = ws.GetSunlightWorld(wx, yy, wz);
			int c = ws.GetCanopyAttenuationWorld(wx, yy, wz);
			col.Append($" y{yy}:s={s},c={c}");
		}
		GD.Print(col.ToString());
	}

	static RegionData SampleRegion(Vector3 playerPos, WorldState ws)
	{
		ChunkState chunk = ws.GetChunk(World.WorldToChunkCoord(playerPos));
		if (chunk == null) { return null; }
		if (ws.Regions == null || chunk.RegionIndex >= ws.Regions.Length) { return null; }
		return ws.Regions[chunk.RegionIndex].Data;
	}

	static float ChunkDistanceXZ(Vector3 a, Vector3 b)
	{
		float dx = (a.X - b.X) / ChunkState.SIZE;
		float dz = (a.Z - b.Z) / ChunkState.SIZE;
		return Mathf.Sqrt(dx * dx + dz * dz);
	}


	// Master gameplay-HUD visibility toggle. Covers BOTH HUD roots: the
	// screen-anchored overlay (`hud`, which contains the minimap widget) and the
	// world-anchored `worldHUD` that parents the floating damage/heal numbers,
	// mob/discoverable labels, and interact prompts. Hiding only `hud` leaves the
	// world-anchored labels drawing, so any mode wanting a clean "no HUD" frame
	// (bird's-eye now, cutscenes / photo mode later) should route through here
	// rather than toggling `hud` directly.
	public void SetHudHidden(bool hidden)
	{
		if (hud != null)
		{
			hud.Visible = !hidden;
		}
		if (worldHUD is CanvasItem worldHudLayer)
		{
			worldHudLayer.Visible = !hidden;
		}
	}

	// Show/hide the in-world UI for the bird's-eye overview shot. Hides the full
	// HUD (via SetHudHidden), the dust motes, and the rain, and drops any live
	// interactive outline + floating prompt. The per-frame highlight gate and
	// UpdateInteractHUD keep the outline/prompt from reappearing while
	// IsBirdsEye is true.
	public void SetBirdsEyeUiHidden(bool hidden)
	{
		SetHudHidden(hidden);
		if (MoteEffect.Current != null)
		{
			MoteEffect.Current.Visible = !hidden;
		}
		if (RainEffect.Current != null)
		{
			RainEffect.Current.Visible = !hidden;
		}
		if (hidden)
		{
			RemoveHighlight();
			if (_interactHUD != null)
			{
				_interactHUD.QueueFree();
				_interactHUD = null;
			}
		}
	}

	// Bumps the screen damage-flash + low-health overlay window. Called from
	// Player.OnHurtBoxHit (direct) and from _PhysicsProcess after each DOT HUD
	// flush; forwards to the ScreenEffectsController that owns the post pass.
	public void FlashDamage(float amount)
	{
		screenEffects?.FlashDamage(amount);
	}

	public override void _PhysicsProcess(double delta)
	{
		base._PhysicsProcess(delta);
	}

	public override void _Input(InputEvent e)
	{
		base._Input(e);
		InputDevice.HandleInputEvent(e);

		// Mouse-motion aim has to live in _Input, not _UnhandledInput: while
		// the cursor is in Captured mode (gameplay), motion events never reach
		// the UnhandledInput tier, so we'd otherwise never see them. Gameplay
		// is gated by the same paused/InputSuppressed/no-player checks the
		// UnhandledInput block uses.
		if (e is InputEventMouseMotion mouseMotion && !paused && !InputSuppressed && _player != null)
		{
			if (flyCamera != null && flyCamera.HandleMouseMotion(mouseMotion))
			{
				return;
			}
			// Virtual aim-stick model: _mousePosition is the deflection of an
			// imaginary cursor around the player, in pixels. Mouse Relative is
			// scaled by sensitivity, accumulated, and clamped to a fixed
			// radius so the cursor lives on a disk. Direction (Directional) or
			// rate-input (Positional) interpretation happens downstream.
			//
			// Gated on _player.IsAiming rather than the raw Aim button so
			// mid-charge mouse motion still reaches the Positional cursor:
			// the player is holding the attack button during charge, not Aim,
			// but IsAiming is forced true through charging (see Player._aiming).
			// Recentering on aim-off (see _Process) makes each aim session
			// start centered, matching gamepad right-stick recentering.
			if (!_player.IsAiming)
			{
				return;
			}
			_mousePosition += mouseMotion.Relative * CVars.mouseSensitivity.Value;
			if (_mousePosition.LengthSquared() > aimCursorRadiusPx * aimCursorRadiusPx)
			{
				_mousePosition = _mousePosition.Normalized() * aimCursorRadiusPx;
			}
			if (_mousePosition.LengthSquared() >= aimCursorDeadzonePx * aimCursorDeadzonePx)
			{
				// Pass the deflection normalized to the disk radius so the
				// magnitude matches the gamepad right-stick convention (0..1).
				// Positional aim integrates this as a rate input; Directional
				// reads only the angle so it doesn't care either way.
				Vector2 deflection01 = _mousePosition / aimCursorRadiusPx;
				_player.ProcessMouseMotion(deflection01, camera.Yaw);
			}
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		base._UnhandledInput(e);

		// Bird's-eye cancel runs before TogglePause because both actions are
		// bound to Escape — when the overlook is active the press should drop
		// the overview, not open the pause menu.
		if (_player != null && _player.IsBirdsEye && e.IsActionPressed("ui_cancel"))
		{
			_player.RequestEndBirdsEye();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (e.IsActionPressed("TogglePause"))
		{
			TogglePause();
			GetViewport().SetInputAsHandled();
			return;
		}

		// While paused, or while any input-consuming modal is up, gameplay
		// input is dropped. Modal-close keys (ui_cancel for map/inventory)
		// fall through to the modal itself in its own _UnhandledInput —
		// see InputSuppressed gate below.
		if (paused || InputSuppressed)
		{
			return;
		}

		if (e.IsActionPressed("Map") && almanacScreen != null)
		{
			almanacScreen.Open(AlmanacScreen.EAlmanacTab.WorldMap, this);
			GetViewport().SetInputAsHandled();
			return;
		}

		if (e.IsActionPressed("Inventory") && almanacScreen != null)
		{
			almanacScreen.Open(AlmanacScreen.EAlmanacTab.Inventory, this);
			GetViewport().SetInputAsHandled();
			return;
		}

		if (e.IsActionPressed("CameraLeft"))
		{
			camera.RotateLeft();
		}

		if (e.IsActionPressed("CameraRight"))
		{
			camera.RotateRight();
		}

		if (e.IsActionPressed("CameraDown"))
		{
			camera.ToggleClipAlways();
		}

	}

	void CullProps(float cameraClip)
	{
		foreach (List<Node3D> entities in _world.ActiveEntities.Values)
		{
			foreach (Node3D entity in entities)
			{
				entity.Visible = entity.GlobalPosition.Y < cameraClip;
			}
		}
	}

	void OnPlayerHighlightChanged(Node3D node)
	{
		RemoveHighlight();
		if (node != null)
		{
			ApplyHighlight(node);
		}
		UpdateInteractHUD();
	}

	// Single source of truth for spawning/freeing the InteractHUD. Called
	// whenever the player's highlight OR current interactive changes: the
	// HUD survives the press-to-start transition (highlight clears the same
	// frame _curInteractive becomes non-null) by binding to whichever target
	// is currently meaningful.
	void UpdateInteractHUD()
	{
		// No interact prompt during the bird's-eye overview shot.
		IInteractive target = (_player?.IsBirdsEye ?? false)
			? null
			: _player?.CurInteractive ?? _player?.HighlightInteractive;
		if (_interactHUD != null && _interactHUD.Interactive != target)
		{
			_interactHUD.QueueFree();
			_interactHUD = null;
		}
		if (target == null)
		{
			return;
		}
		if (_interactHUD == null && interactHudScene != null)
		{
			_interactHUD = InteractHUD.Create(interactHudScene, camera, _player, target, worldHUD);
		}
	}

	// Mesh-based highlight target for solid 3D interactives that have no
	// Sprite3D (statue, sign, chest, ladder). Driven instead of the sprite
	// outline overlay; cleared in RemoveHighlight.
	InteractiveMeshHighlight _meshHighlight;

	void ApplyHighlight(Node3D node)
	{
		Sprite3D source = FindChildSprite(node);
		if (source == null || !source.Visible)
		{
			// No sprite to outline — fall back to the 3D mesh highlight path for
			// solid interactives, toggling their inverted-hull outline via the
			// per-instance `selected` uniform (mirrors the sprite outline gate).
			_meshHighlight = FindMeshHighlight(node);
			_meshHighlight?.SetSelected(true);
			return;
		}

		_highlightOverlay.Texture = source.Texture;
		_highlightOverlay.Transform = Transform3D.Identity;
		_highlightOverlay.Centered = source.Centered;
		_highlightOverlay.Offset = source.Offset;
		_highlightOverlay.PixelSize = source.PixelSize;
		_highlightOverlay.Billboard = source.Billboard;
		_highlightOverlay.TextureFilter = source.TextureFilter;
		// Pick the upright vs flat outline shader based on source type. Both
		// shaders read sprite_texture / sprite_size / sprite_region_origin
		// from material params; the upright one additionally reads
		// forward_offset (which is a no-op on flat sprites).
		bool isFlat = source is FlatLitSprite;
		ShaderMaterial activeOutline = isFlat ? outlineFlatMaterial : outlineMaterial;
		_highlightOverlay.MaterialOverride = activeOutline;
		activeOutline.SetShaderParameter("sprite_texture", source.Texture);
		// Mirror the source sprite's texel addressing so the outline snaps to
		// the same pixel grid as sprite_lit's snapped anchor.
		Vector2I spriteSize;
		Vector2I regionOrigin;
		if (source.RegionEnabled)
		{
			Rect2 r = source.RegionRect;
			spriteSize = new Vector2I((int)r.Size.X, (int)r.Size.Y);
			regionOrigin = new Vector2I((int)r.Position.X, (int)r.Position.Y);
			_highlightOverlay.RegionEnabled = true;
			_highlightOverlay.RegionRect = r;
		}
		else
		{
			spriteSize = new Vector2I(source.Texture.GetWidth(), source.Texture.GetHeight());
			regionOrigin = Vector2I.Zero;
			_highlightOverlay.RegionEnabled = false;
		}
		activeOutline.SetShaderParameter("sprite_size", spriteSize);
		activeOutline.SetShaderParameter("sprite_region_origin", regionOrigin);
		if (!isFlat)
		{
			float forwardOffset = source is LitSprite lit ? lit.ForwardOffset : 0f;
			activeOutline.SetShaderParameter("forward_offset", forwardOffset);
		}
		// Reparent as a child of the source sprite so the overlay inherits
		// its full transform chain — both the parent chain (Mob's MeshContainer
		// drop during burrow) and any sprite-local animation (Loot's bob).
		// Local transform stays identity since the parent IS what we're tracking.
		_highlightOverlay.Reparent(source, false);
		_highlightOverlay.Visible = true;
	}

	void RemoveHighlight()
	{
		if (_meshHighlight != null)
		{
			_meshHighlight.SetSelected(false);
			_meshHighlight = null;
		}
		_highlightOverlay.Visible = false;
		_highlightOverlay.Reparent(sceneViewport, false);
	}

	// Depth-first scan for the first InteractiveMeshHighlight under `node` — the
	// 3D-mesh analog of FindChildSprite. Lets solid interactives route the
	// selection outline to their highlight meshes.
	static InteractiveMeshHighlight FindMeshHighlight(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			if (child is InteractiveMeshHighlight mh)
			{
				return mh;
			}
			InteractiveMeshHighlight nested = FindMeshHighlight(child);
			if (nested != null)
			{
				return nested;
			}
		}
		return null;
	}

	// Depth-first scan for the first visible Sprite3D under `node`. Most
	// interactives (chest, door, torch, ...) author the sprite as a direct
	// child so the first iteration hits. Mob nests its sprite under a
	// MeshContainer for burrow/death transforms, so the recursion is required
	// for mobs to highlight at all.
	static Sprite3D FindChildSprite(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			if (child is Sprite3D sprite && sprite.Visible)
			{
				return sprite;
			}
			Sprite3D nested = FindChildSprite(child);
			if (nested != null)
			{
				return nested;
			}
		}
		return null;
	}

	void OnHudTextRequested(Vector3 position, string text, EHudTextType type)
	{
		if (worldHUD == null) { return; }
		PackedScene scene = GetHudTextScene(type);
		if (scene == null) { return; }
		// Parent under worldHUD (inside GUICanvas) — same place every other
		// world-anchored HUD goes. A Control parented to GameClient (Node3D)
		// has no CanvasLayer ancestor and silently never renders, so we
		// bail above rather than falling back to the wrong parent.
		HudText.Create(scene, _world, camera, position, text, worldHUD);
	}

	// onDamage default subscriber. Rounds the damage payload to an int and
	// invokes onHudText so the floating number renders red. Sub-1 deltas
	// (status-tick chip damage rounded to 0) are dropped — no point spawning
	// a "0" label.
	void OnDamageRequested(Vector3 position, float amount, EHudTextType type)
	{
		int rounded = Mathf.RoundToInt(amount);
		if (rounded <= 0) { return; }
		onHudText?.Invoke(position, rounded.ToString(), type);
	}

	// onHeal default subscriber. Mirrors OnDamageRequested but prepends a '+'
	// so the floating green number reads as a gain rather than just a value.
	void OnHealRequested(Vector3 position, float amount, EHudTextType type)
	{
		int rounded = Mathf.RoundToInt(amount);
		if (rounded <= 0) { return; }
		onHudText?.Invoke(position, "+" + rounded.ToString(), type);
	}

	PackedScene GetHudTextScene(EHudTextType type)
	{
		return type switch
		{
			EHudTextType.Info => hudTextInfoScene,
			EHudTextType.DamageLight => hudTextDamageLightScene,
			EHudTextType.DamageHeavy => hudTextDamageHeavyScene,
			EHudTextType.Crit => hudTextCritScene,
			EHudTextType.Backstab => hudTextBackstabScene,
			EHudTextType.HealLight => hudTextHealLightScene,
			EHudTextType.HealHeavy => hudTextHealHeavyScene,
			_ => null,
		};
	}

	void OnConversationRequested(ConversationData conversation, ConversationContext ctx)
	{
		hud?.ShowConversation(conversation, ctx);
	}

	// Default subscriber for startUpgradeSelection — opens the boon-pick modal
	// with the offered effects and the consumable's apply-on-pick callback.
	// GameClient owns modal visibility + input gating: it shows the picker and
	// gates input/HUD/mouse here. The use is often triggered from the almanac/
	// inventory modal, which would cover the picker, so hide it for the duration
	// and bring it back once the pick resolves (after applying the boon) or the
	// player backs out.
	void OnStartUpgradeSelection(List<BoonData> upgrades, Action<BoonData> onComplete)
	{
		if (upgradeScreen == null)
		{
			return;
		}
		bool restoreAlmanac = almanacScreen != null && almanacScreen.Visible;
		if (restoreAlmanac)
		{
			almanacScreen.Visible = false;
		}
		InputSuppressed = true;
		if (hud != null) { hud.Visible = false; }
		Input.MouseMode = Input.MouseModeEnum.Visible;
		upgradeScreen.Visible = true;

		upgradeScreen.Init(
			chosen =>
			{
				onComplete?.Invoke(chosen);
				CloseUpgradeScreen(restoreAlmanac);
			},
			() => CloseUpgradeScreen(restoreAlmanac),
			FilterViableBoons(upgrades));
	}

	// Number of boon cards the fairy upgrade screen aims to show; the gold
	// filler pads up to this when too few candidate boons are viable.
	const int UpgradeChoiceCount = 3;

	// Narrow the fairy corpse's candidate boons to the ones worth offering right
	// now, then pad to three cards with the gold filler when too few remain.
	// Keeps a restorative boon off the screen at full health and a lasting buff
	// off the screen when already active (see IsBoonViable), so the player never
	// burns a corpse on a no-op pick. The gold filler comes from SimData and is
	// added at most once — it's deliberately absent from the random pool, so it
	// only ever appears here as consolation, never as a random roll.
	List<BoonData> FilterViableBoons(List<BoonData> candidates)
	{
		var viable = new List<BoonData>();
		if (candidates != null)
		{
			for (int i = 0; i < candidates.Count; i++)
			{
				BoonData boon = candidates[i];
				if (boon != null && IsBoonViable(boon))
				{
					viable.Add(boon);
				}
			}
		}
		BoonData gold = _world?.SimData?.FairyBoonGold;
		if (viable.Count < UpgradeChoiceCount && gold != null && !viable.Contains(gold))
		{
			viable.Add(gold);
		}
		return viable;
	}

	// A boon is worth offering when it would actually do something for the
	// player. Restorative boons (heal-to-full / cleanse) are pointless unless the
	// player is injured or afflicted; a lasting status-effect buff is pointless
	// if the player already carries it. An item-only boon (gold) has no lasting
	// effect to already-have, so it's always viable. Data-driven so new boons
	// slot in without touching this gate.
	bool IsBoonViable(BoonData boon)
	{
		if (_player == null)
		{
			return true;
		}
		StatusEffectData effect = boon.statusEffect;
		if (effect == null)
		{
			return true;
		}
		// A heal / cleanse boon is worth offering when it would do something: the
		// player is injured, or carries one of the afflictions it would remove.
		bool heals = effect.instantHealPercent > 0f;
		bool cleanses = effect.removesOnApply != null && effect.removesOnApply.Count > 0;
		if (heals || cleanses)
		{
			return _player.IsInjured || HasAny(effect.removesOnApply);
		}
		if (!effect.instantaneous)
		{
			return !_player.HasStatusEffect(effect);
		}
		return true;
	}

	// True when the player currently has any of `effects` active.
	bool HasAny(Godot.Collections.Array<StatusEffectData> effects)
	{
		if (effects == null)
		{
			return false;
		}
		for (int i = 0; i < effects.Count; i++)
		{
			if (effects[i] != null && _player.HasStatusEffect(effects[i]))
			{
				return true;
			}
		}
		return false;
	}

	// Tear down the boon-pick modal: hide it and either hand control back to the
	// almanac modal it was launched from (keeping input gated) or return to
	// normal gameplay when it was used straight from the hotbar.
	void CloseUpgradeScreen(bool restoreAlmanac)
	{
		if (upgradeScreen != null)
		{
			upgradeScreen.Visible = false;
		}
		if (restoreAlmanac && almanacScreen != null)
		{
			almanacScreen.Visible = true;
			InputSuppressed = true;
			if (hud != null) { hud.Visible = false; }
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}
		else
		{
			InputSuppressed = false;
			if (hud != null) { hud.Visible = true; }
			Input.MouseMode = Input.MouseModeEnum.Captured;
		}
	}

	void OnPlayerInteractChanged(IInteractive interactive)
	{
		UpdateInteractHUD();
	}

	void OnMobSpawned(Mob mob)
	{
		if (mob.HudScene != null)
		{
			MobHUD.Create(mob.HudScene, camera, mob, worldHUD);
		}
	}

	void OnMobRemoved(Mob mob)
	{
	}

	void OnDiscoverableSpawned(Discoverable discoverable)
	{
		if (discoverable.HudScene != null)
		{
			DiscoverableHud.Create(discoverable.HudScene, camera, discoverable, worldHUD);
		}
	}

	public void TogglePause()
	{
		paused = !paused;
		onPauseToggled?.Invoke(paused);
	}

	// CombatTracker victory bridge: combat ended by killing the last perceived
	// threat. Forwards to subscribers (music plays its victory sting), punches
	// the finisher slow-mo + zoom, and focuses the camera on the finishing
	// blow's victim; all auto-release after combatVictorySlowMoSeconds.
	void OnCombatVictory(Mob killedMob)
	{
		onCombatVictory?.Invoke();
		slowMotion?.Trigger();
		if (killedMob != null)
		{
			camera?.FocusOn(killedMob);
		}
		_victorySlowMoReleaseMs = Time.GetTicksMsec() + (ulong)(combatVictorySlowMoSeconds * 1000f);
	}

	// Player.onDied bridge. Suppress gameplay input for the entire death
	// sequence (fade-out → prompt → fade-in); DeathScreen clears the gate
	// at the end of its fade-in. Notify subscribers, then hand control to
	// the DeathScreen for the visual + audio sequence.
	void OnPlayerDiedInternal(Player player)
	{
		InputSuppressed = true;
		onPlayerDied?.Invoke(player);

		// Hand the heartbeat over to its death wind-down (latched live rate,
		// eased to a stop synced to the DeathScreen fade). 0 → the controller
		// uses its own lowHealthDeathSlowdownSeconds fallback.
		screenEffects?.NotifyPlayerDied(deathScreen?.fadeOutSeconds ?? 0f);

		// Punch into slow-motion + zoom and hold it through the death-screen
		// fade; RespawnPlayer releases it. Cancel any pending finisher auto-
		// release so it can't cut this hold short.
		_victorySlowMoReleaseMs = 0;
		slowMotion?.Trigger();
		// A finisher focus could still be panning to a corpse when the player
		// dies — snap framing intent back to the player for the death cam.
		camera?.ClearFocus();

		if (deathScreen != null)
		{
			deathScreen.Show(this);
		}
		else
		{
			// No screen wired (unit-test scaffolding): respawn immediately
			// so the gate doesn't strand input forever.
			RespawnPlayer();
			InputSuppressed = false;
		}
	}

	// Called from DeathScreen when the player accepts the respawn prompt.
	// Resets player pools / status effects, hard-teleports to the spawn
	// point, and snaps the camera so the first frame of the fade-in already
	// shows the spawn position rather than tween-lerping from the death
	// site. Input stays suppressed by DeathScreen until its fade-in
	// completes.
	public void RespawnPlayer()
	{
		if (_player == null)
		{
			return;
		}
		_player.Respawn(_spawnPosition);
		camera.SetInitialPosition(_spawnPosition);

		// Ease back to real time + the resting zoom. The ease-out plays under the
		// DeathScreen fade-in (revealing from black).
		slowMotion?.Release();

		// Clear the death wind-down so the heartbeat goes fully idle (health is
		// restored, so the overlay ramp is 0); a fresh low-health episode will
		// re-engage it from scratch.
		screenEffects?.ResetOnRespawn();
	}

	// True while the player is dead — read by SleepOverlay to decide whether to
	// wake the sleeper or hand the screen to the DeathScreen.
	public bool PlayerIsDead => _player?.IsDead ?? false;

	// True once the DeathScreen has fully faded to black (its Prompt hold). The
	// SleepOverlay waits for this before releasing on a die-in-sleep so the swap
	// between the two black overlays shows no frame of the world. No DeathScreen
	// wired (test scaffolding) reads as opaque so the overlay never strands.
	public bool DeathScreenOpaque => deathScreen == null || deathScreen.State == DeathScreen.EState.Prompt;

	// Tent / rest entry point. Fades to black, skips world time, then fades
	// back in (or hands off to the death sequence if a status effect proved
	// lethal during the skip). Input stays suppressed for the whole sequence;
	// SleepOverlay releases it via EndSleep on a clean wake, or leaves it to the
	// DeathScreen on a die-in-sleep.
	public void BeginSleep(double hours)
	{
		if (_player == null || sleepOverlay == null || sleepOverlay.Busy || InputSuppressed)
		{
			return;
		}
		InputSuppressed = true;
		sleepOverlay.Show(this, hours);
	}

	// Called by SleepOverlay once the screen is fully black — the only moment
	// the skip is visible-safe (so an integrated DoT death and its slow-mo
	// death-cam happen behind the curtain).
	public void PerformSleepAdvance(double hours)
	{
		_world?.AdvanceTime(hours);
	}

	// Called by SleepOverlay when a clean wake's fade-in completes.
	public void EndSleep()
	{
		InputSuppressed = false;
	}

	public void Save()
	{
		SaveGame.Save(CVars.savePath.Value);
	}

	public void QuitToMenu()
	{
		onQuitToMenu?.Invoke();
	}

}
