using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

public partial class GameClient : Node3D
{
	public static GameClient Current { get; private set; }

	// UI display strings for the inventory's per-action / per-context stat
	// readouts. Centralized here so a future localization pass swaps them
	// in one place instead of chasing string literals through every panel.
	public readonly Dictionary<EStatName, string> statNames = new Dictionary<EStatName, string>
	{
		{ EStatName.Damage, "Damage" },
		{ EStatName.Pierce, "Pierce" },
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
	[Export] public Hud hud;
	[Export] public AlmanacScreen almanacScreen;
	[Export] public CookingScreen cookingScreen;
	[Export] public MerchantScreen merchantScreen;
	[Export] public StashScreen stashScreen;
	[Export] public DeathScreen deathScreen;
	[Export] public Node worldHUD;
	[Export] public SubViewport sceneViewport;
	[Export] public MeshInstance3D bloomQuad;
	[Export] public ShaderMaterial upscaleMaterial;
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
	[Export] public ShaderMaterial postProcessMaterial;

	[ExportGroup("Damage Feedback")]
	// Red-flash intensity = (damage / maxHealth) * scale, clamped to 1.
	// A scale of 2 means a 50% chunk drives the flash to its max; tune up
	// to make smaller chips more visible.
	[Export(PropertyHint.Range, "0.1,8,0.1")] public float damageFlashScale = 2f;
	// Seconds for the flash to decay from 1 → 0. Decay is linear; tune by
	// feel against the typical hit cadence.
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float damageFlashFadeSeconds = 0.4f;
	// Optional vignette mask. When null the shader's hint_default_white
	// drives a uniform red overlay — assign a soft-edged radial PNG to
	// make damage "bleed in from the screen edges".
	[Export] public Texture2D damageFlashTexture;
	[Export] public Color damageFlashColor = new Color(1f, 0.05f, 0.05f, 1f);
	// Health fraction below which the low-health overlay starts to ramp.
	// 0.333 = enters at 1/3 health; at 0 health the overlay is full
	// strength against the per-component max below.
	[Export(PropertyHint.Range, "0,1,0.01")] public float lowHealthThreshold = 1f / 3f;
	// Maximum desaturation and dim at 0 health. The ramp from
	// lowHealthThreshold → 0 health interpolates these toward 0 → max.
	[Export(PropertyHint.Range, "0,1,0.01")] public float lowHealthMaxDesaturation = 0.85f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float lowHealthMaxDim = 0.35f;
	// The whole low-health overlay (desat + dim + heartbeat) only lingers for
	// a window after the last hit, then fades out — so a player who survived a
	// scare isn't stuck staring at a grey screen. Taking damage refills the
	// timer to full, snapping the effect back to its nearness-to-death
	// intensity. This is the fade duration / window length in seconds.
	[Export(PropertyHint.Range, "1,30,0.5")] public float lowHealthEffectSeconds = 10f;

	// Heartbeat thump on the low-health overlay. Once the ramp is active the
	// screen pulses on a lub-dub cadence — the desaturation breathes (color
	// bleeds back a touch per thump) and a faint red tint surges — at a rate
	// that climbs from `Slow` at the threshold to `Fast` at 0 health. The
	// heartbeat SFX retriggers on each cycle.
	[Export(PropertyHint.Range, "20,200,1")] public float lowHealthHeartbeatSlowBpm = 55f;
	[Export(PropertyHint.Range, "20,260,1")] public float lowHealthHeartbeatFastBpm = 150f;
	// Fraction of the current desaturation the world's color regains at the
	// peak of each thump — the visible "breath" of the pulse.
	[Export(PropertyHint.Range, "0,1,0.01")] public float lowHealthHeartbeatDesaturationPulse = 0.35f;
	// Peak red tint mixed in at the crest of each thump.
	[Export(PropertyHint.Range, "0,1,0.01")] public float lowHealthHeartbeatRedTint = 0.22f;
	[Export] public Color lowHealthHeartbeatColor = new Color(0.5f, 0f, 0f);
	// On death the heartbeat decelerates from its live rate to a full stop —
	// and the thump tint fades out — over this window. Sourced from the
	// DeathScreen's fade-out time when one is wired so the heart and the
	// screen wind down together; this is the fallback when none is.
	[Export(PropertyHint.Range, "0.5,8,0.1")] public float lowHealthDeathSlowdownSeconds = 3f;
	// Heartbeat SFX, retriggered once per lub-dub cycle. Non-spatial (the
	// player's own heart) — wired to an AudioStreamPlayer on the Master bus
	// so the DeathScreen's World3D fade doesn't silence it mid-wind-down.
	[Export] public AudioStreamPlayer heartbeatAudio;
	[Export(PropertyHint.Range, "-40,6,0.5")] public float lowHealthHeartbeatVolumeDb = -4f;
	// Pitch climbs toward this at 0 health (adrenaline), then drifts down as
	// the heartbeat slows to a stop on death.
	[Export(PropertyHint.Range, "1,2,0.01")] public float lowHealthHeartbeatMaxPitch = 1.2f;
	[ExportGroup("")]
	// Aim-cursor saturation radius (pixels). Larger = more mouse travel
	// before the virtual cursor reaches the edge of its disk, so the aim
	// direction takes longer to swing. Direction-only after this — atan2
	// in Player ignores magnitude.
	const float AIM_CURSOR_RADIUS_PX = 200f;
	// Below this magnitude the accumulator is treated as "at rest" and the
	// player's aim direction is left alone. Stops sub-pixel jitter from
	// continuously re-aiming when the player is trying to hold steady.
	const float AIM_CURSOR_DEADZONE_PX = 5f;

	[ExportGroup("Minimap")]
	// Slice-view color for solid-rock columns. Painted at the reserved
	// MinimapData.WallSlotIndex slot in the tile LUT; kit-agnostic so a
	// tunnel through any biome reads as the same dark grey.
	[Export] public Color minimapWallSlotColor = new Color(0.045f, 0.045f, 0.05f);
	// Color palette for foliage stamps on the minimap.
	[Export] public MinimapFoliageColors minimapFoliageColors;
	// Visual zoom: how many minimap-source pixels each world meter occupies
	// on the rendered TextureRect. Higher = more zoomed in. Independent of
	// player vision — purely presentation.
	[Export(PropertyHint.Range, "0.25,16,0.25")] public float minimapPixelsPerMeter = 2f;
	// Indoor zoom-in multiplier on top of minimapPixelsPerMeter — 2.0 = 2×
	// closer indoors, useful for corridors. Presentation only; doesn't
	// affect what the player perceives.
	[Export(PropertyHint.Range, "0.5,8,0.25")] public float minimapIndoorZoom = 2f;
	// Reveal radius (what the player perceives) = vision × this. Drives
	// both the outdoor surface mask and the indoor active-slice mask;
	// independent of zoom because how far you see doesn't depend on how
	// the map is rendered.
	[Export(PropertyHint.Range, "0.5,10,0.1")] public float minimapRevealMultiplier = 1.5f;
	// Soft-edge inner-fraction for every reveal disk. Inside `radius * this`
	// the disk paints at full brightness; from there to the outer radius
	// the value falls linearly to 0. 1.0 = hard edge, ~0.5 = wide soft fade.
	[Export(PropertyHint.Range, "0.1,1,0.05")] public float minimapRevealInnerFraction = 0.7f;

	[ExportGroup("Heat Shimmer")]
	// Texture side length (cells). Locked at boot — HeatField allocates the
	// ImageTexture in _Ready. Larger = sharper disk edges + finer gradient;
	// cost is N*N bytes per per-frame upload.
	[Export(PropertyHint.Range, "32,1024,1")] public int heatShimmerResolution = 256;
	// Total side length in meters covered by the heat field. Centered on the
	// player; field UVs are 0 at (player − size/2) and 1 at (player + size/2).
	[Export(PropertyHint.Range, "8,512,1")] public float heatShimmerSizeMeters = 64f;
	// Ambient air-temperature ramp (°F). Below START = no shimmer, above
	// FULL = max shimmer; linear interpolation between.
	[Export(PropertyHint.Range, "0,200,0.5")] public float heatShimmerAmbientStartF = 90f;
	[Export(PropertyHint.Range, "0,200,0.5")] public float heatShimmerAmbientFullF = 120f;
	// WarmthZone shimmer intensity = clamp(warmingTemperature / divisor, 0, 1).
	// 30°F warming hits ~1.0 intensity; the 20°F campfire default lands at ~0.67.
	[Export(PropertyHint.Range, "1,200,0.5")] public float heatShimmerWarmIntensityDivisor = 30f;
	// Inner fraction of stamped disks that paints at full intensity. Outside
	// this fraction falls linearly to 0 at the disk edge.
	[Export(PropertyHint.Range, "0,1,0.05")] public float heatShimmerDiskInnerFraction = 0.5f;

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

	// Sample wind speed in m/s at `worldPos`. Returns 0 when the voxel sun
	// BFS reports no skylight at all — a stand-in for "the player is in a
	// cave or under a roof", where the open-sky wind from the weather
	// system shouldn't reach them. Permissive: BFS spreads sideways from
	// open columns, so a cave mouth or doorway still seeps wind. Same
	// shape as SampleAirTemperature so callers can ignore wind whenever
	// they ignore weather.
	public float SampleWindSpeed(Vector3 worldPos)
	{
		SkyController sky = SkyController.Current;
		if (sky?.Weather == null) { return 0f; }
		float wind = sky.Weather.windSpeed;
		if (wind <= 0f) { return 0f; }

		WorldState ws = World.Current?.WorldState;
		if (ws != null && ws.GetSkyLight01(worldPos) <= 0f)
		{
			return 0f;
		}
		return wind;
	}

	// Per-component breakdown of the air-temperature sample. The `temp`
	// console CVar prints these so weather / lighting / occlusion can be
	// inspected independently. Final temperature is `Total`.
	public struct AirTemperatureSample
	{
		public float air;             // weather.airTemperature (°F, base ambient)
		public float sunTemperature;  // weather.sunTemperature (°F, max sun add)
		public float sunFactor;       // sky.SunFactor (time-of-day, 0..1)
		public float cloudCover;      // weather.cloudCover (0..1)
		public float fog;             // sky.Palette.Fog (0..1)
		public float skyTransmission; // 1 − clamp(cloudCover + fog, 0, 1)
		public float sunMask;         // sunBfs / LightEngine.MAX_LIGHT (0..1)

		public readonly float SunContribution => sunTemperature * sunFactor * skyTransmission * sunMask;
		public readonly float Total => air + SunContribution;
	}

	// Sample environmental air temperature in degrees F at `worldPos`.
	// airTemperature flows through unconditionally; sunTemperature stacks on
	// scaled by (a) sun strength now, (b) atmospheric transmission (clouds +
	// fog), and (c) the voxel sunlight BFS mask at the sample point — so
	// overhangs, caves, and foliage shade the sun's heating exactly the way
	// the world's lighting pass already classifies them. Player.cs adds its
	// own warmth-zone bonus on top of this — campfires are not sampled here
	// because the player tracks zone enter/exit directly.
	public float SampleAirTemperature(Vector3 worldPos)
	{
		return SampleAirTemperatureBreakdown(worldPos).Total;
	}

	public AirTemperatureSample SampleAirTemperatureBreakdown(Vector3 worldPos)
	{
		AirTemperatureSample s = default;
		SkyController sky = SkyController.Current;
		if (sky == null) { s.air = 64.4f; return s; }
		WeatherData weather = sky.Weather;
		if (weather == null) { s.air = 64.4f; return s; }

		s.air = weather.airTemperature;
		s.sunTemperature = weather.sunTemperature;
		s.sunFactor = sky.SunFactor;
		s.cloudCover = weather.cloudCover;
		s.fog = sky.Palette.Fog;
		// Atmospheric attenuation. Cloud cover (weather) and fog (palette,
		// derived from humidity + cool diurnal) each occlude the sun
		// independently; their sum is clamped to 1 so a fully overcast OR
		// fully foggy sky drives the multiplier to 0 without going negative
		// when both pile up.
		s.skyTransmission = 1f - Mathf.Clamp(s.cloudCover + s.fog, 0f, 1f);

		s.sunMask = 1f;
		WorldState ws = World.Current?.WorldState;
		if (ws != null)
		{
			s.sunMask = ws.GetSkyLight01(worldPos);
		}
		return s;
	}

	public Action onInit;
	public Action<Player> onPlayerSpawned;
	// Fires once when the player crosses to dead — Hud and any other
	// subscribers can react alongside the DeathScreen sequence the client
	// drives directly.
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
	public Action<bool> onPauseToggled;
	public Action onQuitToMenu;

	// Fired when the player enters a named region (CurrentRegion null →
	// non-null OR → a different non-null region). Border chunks (RegionIndex
	// points at a Regions[] entry whose Data is null) keep CurrentRegion
	// sticky; clearing back to null on extended border travel is silent so
	// the next named region's entry pulses the banner cleanly.
	public Action<RegionData> onRegionEntered;
	public RegionData CurrentRegion { get; private set; }

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

	// Region-entry hysteresis. Wiggling on a seam mustn't flicker the
	// banner; an intentional crossing should fire within a step or two;
	// a chain of border zones can't keep the player tagged with a region
	// they've walked far away from. UpdateRegion runs the state machine
	// each tick.
	const float REGION_DWELL_SECONDS = 1.5f;
	const float REGION_ENTER_DISTANCE_CHUNKS = 1.0f;
	// A bit larger than ZoneBlend.BlendRadiusChunks (= 2) so the visible
	// cross-blend band is fully inside the sticky range.
	const float REGION_BORDER_TRAVEL_CHUNKS = 3.0f;
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
	// Flat plane mesh that renders clouds from above when the camera clears
	// the cloud layer. Hosted under sceneViewport (not SkyController, which
	// is outside the SubViewport that owns the main camera) and follows the
	// camera XZ at world y=SkyController.EffectiveCloudAltitude each frame.
	MeshInstance3D _cloudOverheadPlane;
	ShaderMaterial _cloudOverheadMaterial;
	InteractHUD _interactHUD;
	Vector2 _subpixelTexelOffset;

	// Per-frame entity-spawn budget for the loading-screen-opaque window.
	// World defaults to 8/frame for hitch-free in-game streaming; 64 burns
	// through the inner sphere in a fraction of a second since the player
	// can't see the frame hitches behind the overlay. Reset to the default
	// before the fade so post-fade pop-in stays smooth.
	const int LOADING_ENTITY_SPAWN_BURST = 64;

	const float FLYCAM_SPEED = 20f;
	const float FLYCAM_BOOST = 5f;
	const float FLYCAM_LOOK_SENSITIVITY = 0.005f;
	float _flyYaw;
	float _flyPitch;
	bool _flyInitialized;

	// Bird's-eye view driver. The player fires onBirdsEye(true/false); we run
	// a three-phase state machine (FlyUp → Steady → FlyDown) that lifts the
	// camera off the player and zooms the orthographic Size out, then reverses
	// on cancel. Motion blur is driven only during FlyUp — the shader uniform
	// is combined max-of with the camera's rotation blur in UpdatePostProcess.
	// Movement-lock release waits for Player.OnBirdsEyeReturnComplete, called
	// from this driver when FlyDown lands back at base.
	[ExportGroup("Bird's Eye")]
	// Vertical lift (world meters) added to the camera's normal offset at the
	// top of the FlyUp. Orthographic projection so altitude doesn't change
	// scale on its own — paired with the size multiplier below for the zoom.
	[Export(PropertyHint.Range, "0,400,1,or_greater")] public float birdsEyeAltitude = 80f;
	// Degrees to steepen (lower) the camera pitch at the apex, eased in
	// alongside the lift and zoom. The camera's resting pitchDegrees is
	// negative (looking down), so we subtract this to tilt further toward
	// straight-down for the overview. Reverses on FlyDown.
	[Export(PropertyHint.Range, "0,45,1,or_greater")] public float birdsEyePitchDelta = 15f;
	// Multiplier on the camera's base orthographic Size at the apex. 4× takes
	// the default ~10m vertical extent out to ~40m so a sizable chunk of the
	// surrounding chunks is on-screen. This is the "zoom out" knob; combined
	// with birdsEyeAltitude it controls how big the overview reads.
	[Export(PropertyHint.Range, "1,16,0.25,or_greater")] public float birdsEyeSizeMultiplier = 4f;
	// Wall-clock seconds for either transition (fly-up and fly-down match).
	[Export(PropertyHint.Range, "0.25,5,0.05")] public float birdsEyeTransitionSeconds = 1.5f;
	// Peak motion-blur strength during FlyUp. 1 = max (heavy smear), 0 = no
	// blur. Tapers to 0 at the apex via sin(πt) regardless of peak.
	[Export(PropertyHint.Range, "0,1,0.05")] public float birdsEyeMotionBlurPeak = 1f;
	// Fog visibility multiplier at the apex. Stretches fog_max_distance and
	// thins both fog densities by 1/scale so the overview isn't smothered
	// by ground-level fog. Lerps from 1 (ground) to this at full lift via
	// SkyController.FogVisibilityScale.
	[Export(PropertyHint.Range, "1,16,0.25,or_greater")] public float birdsEyeFogVisibilityScale = 4f;

	enum EBirdsEyePhase { None, FlyUp, Steady, FlyDown }
	EBirdsEyePhase _birdsEyePhase = EBirdsEyePhase.None;
	float _birdsEyeElapsed;
	float _birdsEyeBaseSize;
	float _birdsEyeBlur;
	// Screen-space motion-blur direction during fly-up. The camera rises in
	// world; objects sweep downward on screen (Godot SCREEN_UV has Y=0 at top),
	// so the blur trails toward +Y. Same convention as the camera's RotateLeft.
	static readonly Vector2 BIRDS_EYE_BLUR_DIR = new Vector2(0f, 1f);

	public int PixelScale => Math.Max(1, CVars.pixelScale.Value);

	public Vector2 ProjectToScreen(Vector3 worldPos)
	{
		// The upscale shader flips V (sample at 1 - inner_uv.y) to
		// compensate for Godot's Y-up viewport texture storage. That flip
		// inverts the direction of uv_offset.y relative to uv_offset.x, so
		// the Y correction here adds the subpixel offset where X subtracts.
		Vector2 innerPx = camera.UnprojectPosition(worldPos);
		return new Vector2(
			(innerPx.X - _subpixelTexelOffset.X) * PixelScale,
			(innerPx.Y + _subpixelTexelOffset.Y) * PixelScale);
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

		// 2D screen-space cloud quad — fullscreen NDC pass that samples two
		// noise reads per pixel (base offset + coverage) to render a cloud
		// layer bounded to [50%, 75%] of player→camera height. Same shape
		// as the in-scene FogQuad: a QuadMesh at 2×2, parented to the camera
		// at local (0,0,-6) so the mesh follows the camera and stays inside
		// the frustum. The shader emits POSITION = (VERTEX.xy * 2, 1, 1) so
		// the world transform doesn't matter for fragment placement — only
		// for AABB-based culling. Cost is bounded to the overlook scene by
		// the Visible gate.
		var cloudShader = GD.Load<Shader>("res://shaders/clouds_overhead.gdshader");
		_cloudOverheadMaterial = new ShaderMaterial();
		_cloudOverheadMaterial.Shader = cloudShader;
		var cloudQuadMesh = new QuadMesh();
		cloudQuadMesh.Size = new Vector2(2f, 2f);
		_cloudOverheadPlane = new MeshInstance3D();
		_cloudOverheadPlane.Name = "CloudQuad";
		_cloudOverheadPlane.Mesh = cloudQuadMesh;
		_cloudOverheadPlane.MaterialOverride = _cloudOverheadMaterial;
		_cloudOverheadPlane.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		_cloudOverheadPlane.ExtraCullMargin = 16384f;
		_cloudOverheadPlane.Visible = false;
		camera.AddChild(_cloudOverheadPlane);
		_cloudOverheadPlane.Position = new Vector3(0f, 0f, -6f);

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
		_inputSuppressed = false;
		_inputSuppressClearPending = false;

		GetTree().Root.SizeChanged += UpdateViewportSize;
		UpdateViewportSize();

		if (upscaleMaterial != null)
		{
			upscaleMaterial.SetShaderParameter("inner_tex", sceneViewport.GetTexture());
		}
	}

	public async void Init(Vector3 playerPosition, PackedScene playerScene, PlayerSpawnData playerSpawnData, WorldState worldState, LoadingScreen loadingScreen = null)
	{
		_spawnPosition = playerPosition;
		onHudText += OnHudTextRequested;
		onDamage += OnDamageRequested;
		onHeal += OnHealRequested;
		onConversation += OnConversationRequested;
		onInit?.Invoke();

		// The loading screen owned by Main is up and currently sitting on
		// the chunk-fill phase (~60%). We keep gameplay input suppressed
		// for the rest of the load and hand it back when the screen fades.
		InputSuppressed = true;

		var phaseSw = Stopwatch.StartNew();
		_world = new World();
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
		_player.onBirdsEye += OnPlayerBirdsEye;
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
		_world.MaxEntitiesPerFrame = LOADING_ENTITY_SPAWN_BURST;
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

// Push radius and bend strength for the detail-sprite shader's player
	// reaction. ~0.6m matches the player's foot footprint; 0.25m bend reads
	// as grass parting around the player's legs without snapping flat.
	private const float DETAIL_PLAYER_RADIUS = 0.6f;
	private const float DETAIL_PLAYER_STRENGTH = 0.25f;

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

		if (_birdsEyePhase == EBirdsEyePhase.FlyUp || _birdsEyePhase == EBirdsEyePhase.Steady)
		{
			// Entering the bird's-eye overlook (started by BirdsEyeEffect): the
			// camera lifts overhead, so the iso-angle camera→player fade tube
			// must close. Skip the probe and clamp the live activation down to
			// a ceiling that tracks the FlyUp vertical lift — (1-t)² while
			// rising (i.e. 1 minus the lift's own eased curve), 0 at the apex —
			// so the dithered iris contracts in lockstep with the lift and is
			// fully gone exactly when the lift completes. min() means it only
			// ever shrinks here, never re-widens mid-contraction.
			float ceiling;
			if (_birdsEyePhase == EBirdsEyePhase.FlyUp)
			{
				float duration = Mathf.Max(0.0001f, birdsEyeTransitionSeconds);
				float t = Mathf.Min(1f, _birdsEyeElapsed / duration);
				ceiling = (1f - t) * (1f - t);
			}
			else
			{
				ceiling = 0f;
			}
			_foliageFadeActivationAmount = Mathf.Min(_foliageFadeActivationAmount, ceiling);
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
			World.FadeProbeResult probeResult = world != null
				? world.ProbeFadeVolume(cameraWorld, feet, head, tightProbeRadius, wideProbeRadius, foliagePlayerFadeProbeRange, out nearbyPropCount)
				: World.FadeProbeResult.None;

			float target;
			if (probeResult == World.FadeProbeResult.Tight)
			{
				// Saturation point of 1 means a single nearby tree already hits
				// full radius — guard so the divide can't go negative.
				int saturate = Mathf.Max(foliagePlayerFadeCountScaleSaturate, 1);
				float countNorm = Mathf.Clamp((nearbyPropCount - 1) / (float)Mathf.Max(saturate - 1, 1), 0f, 1f);
				target = Mathf.Lerp(foliagePlayerFadeCountScaleMin, 1f, countNorm);
			}
			else if (probeResult == World.FadeProbeResult.Wide)
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
		RenderingServer.GlobalShaderParameterSet("player_radius", DETAIL_PLAYER_RADIUS);
		RenderingServer.GlobalShaderParameterSet("player_strength", DETAIL_PLAYER_STRENGTH);

		if (_birdsEyePhase != EBirdsEyePhase.None)
		{
			UpdateBirdsEyeCamera(deltaTime);
			SnapCameraAndUpdateUpscale();
			// Sprites are sized off `sprite_chunky` (world meters per inner-viewport
			// texel) — SnapCameraAndUpdateUpscale ties it to the live ortho Size so
			// the pixel-art look stays "1 source pixel = N screen pixels". During
			// the fly-up we WANT sprites to shrink with the zoom, so re-anchor the
			// uniform to the pre-zoom Size. Snap math has already run against the
			// live (inflated) chunky, so the camera's grid stays consistent; only
			// the sprite scaler is reverted. Sub-pixel sprite rendering during the
			// overview is the explicit tradeoff for a view that actually reads as
			// zoomed out.
			ApplyBirdsEyeSpriteChunky();
			CullProps(camera.Clip);
		}
		else if (CVars.debugFlyCam.Value)
		{
			UpdateFlyCamera(deltaTime);
			CullProps(float.PositiveInfinity);
		}
		else
		{
			_flyInitialized = false;
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
			SnapCameraAndUpdateUpscale();
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
		UpdatePostProcess();

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
			bool shouldShow = _player?.HighlightInteractive != null && !externalHudActive && _meshHighlight == null;
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
	//     accumulates; commit the swap (and fire onRegionEntered) once
	//     the player has stayed in the candidate's chunks for
	//     REGION_DWELL_SECONDS or moved REGION_ENTER_DISTANCE_CHUNKS
	//     past where the dwell started.
	//   - Underfoot chunk is a border (Regions[i].Data == null):
	//     CurrentRegion stays put until the player has traveled
	//     REGION_BORDER_TRAVEL_CHUNKS from where they entered, then
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
				if (ChunkDistanceXZ(playerPos, _currentRegionEnterPos) > REGION_BORDER_TRAVEL_CHUNKS)
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

		bool dwellMet = _pendingRegionElapsed >= REGION_DWELL_SECONDS;
		bool distMet = ChunkDistanceXZ(playerPos, _pendingRegionEnterPos) >= REGION_ENTER_DISTANCE_CHUNKS;
		if (dwellMet || distMet)
		{
			CurrentRegion = candidate;
			_currentRegionEnterPos = playerPos;
			_pendingRegion = null;
			_pendingRegionElapsed = 0f;
			ws.SimState.DiscoveredRegions.Add(CurrentRegion);
			onRegionEntered?.Invoke(CurrentRegion);
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

	void UpdateFlyCamera(double deltaTime)
	{
		if (!_flyInitialized)
		{
			Vector3 rot = camera.GlobalRotation;
			_flyPitch = rot.X;
			_flyYaw = rot.Y;
			camera.SetClip(float.PositiveInfinity, camera.GlobalPosition);
			_flyInitialized = true;
		}

		float dt = (float)deltaTime;
		Vector3 move = Vector3.Zero;
		if (Input.IsPhysicalKeyPressed(Key.W)) { move.Z -= 1f; }
		if (Input.IsPhysicalKeyPressed(Key.S)) { move.Z += 1f; }
		if (Input.IsPhysicalKeyPressed(Key.A)) { move.X -= 1f; }
		if (Input.IsPhysicalKeyPressed(Key.D)) { move.X += 1f; }
		if (Input.IsPhysicalKeyPressed(Key.Space)) { move.Y += 1f; }
		if (Input.IsPhysicalKeyPressed(Key.Ctrl)) { move.Y -= 1f; }

		float speed = FLYCAM_SPEED;
		if (Input.IsPhysicalKeyPressed(Key.Shift)) { speed *= FLYCAM_BOOST; }

		camera.GlobalRotation = new Vector3(_flyPitch, _flyYaw, 0);
		if (move.LengthSquared() > 0f)
		{
			Basis basis = camera.GlobalBasis;
			Vector3 worldMove = (basis.X * move.X + basis.Z * move.Z) + Vector3.Up * move.Y;
			camera.GlobalPosition += worldMove.Normalized() * speed * dt;
		}
	}

	void OnPlayerBirdsEye(bool active)
	{
		if (active)
		{
			// Capture the resting ortho Size so the fly-up zoom and the fly-down
			// snap-back both lerp against the same anchor — CVar tweaks to the
			// base size during the overlook don't strand the camera zoomed in.
			_birdsEyeBaseSize = camera.Size;
			_birdsEyePhase = EBirdsEyePhase.FlyUp;
			_birdsEyeElapsed = 0f;
			camera.ManualClipMode = true;
			// Force the indoor cutaway off so the camera can see the world from
			// above even if the player started under a roof. SetClip routes
			// through the existing fade so the ceiling cap dissolves smoothly.
			// ClipAlways=false drops the user-toggled cutaway too, and stays
			// false after FlyDown so the player has to re-enable it manually
			// if they want it back.
			camera.ClipAlways = false;
			camera.SetClip(float.PositiveInfinity, _player.GlobalPosition);
			// Cloud quad renders only while the overview is active AND the
			// `clouds` CVar is enabled. UpdateBirdsEyeCamera re-checks the
			// CVar every frame so toggling it mid-overlook updates live.
			if (_cloudOverheadPlane != null)
			{
				_cloudOverheadPlane.Visible = CVars.clouds.Value;
			}
		}
		else
		{
			if (_birdsEyePhase == EBirdsEyePhase.None)
			{
				return;
			}
			// Cancelling mid-FlyUp: seed _birdsEyeElapsed so FlyDown picks up at
			// the current eased height instead of snapping. FlyUp eases toward
			// the apex as 1-(1-t)^2; FlyDown eases toward the ground as t^2 (t
			// runs 1→0). Solve t^2 = easedNow for the FlyDown start fraction so
			// the height is continuous. From Steady (easedNow=1) this lands on
			// elapsed=0 naturally.
			float duration = Mathf.Max(0.0001f, birdsEyeTransitionSeconds);
			float easedNow = 1f;
			if (_birdsEyePhase == EBirdsEyePhase.FlyUp)
			{
				float currentT = Mathf.Clamp(_birdsEyeElapsed / duration, 0f, 1f);
				easedNow = 1f - (1f - currentT) * (1f - currentT);
			}
			// FlyDown's per-frame t = 1 - elapsed/duration, so to start at
			// t = sqrt(easedNow) we seed elapsed = (1 - sqrt(easedNow))·duration.
			float startT = Mathf.Sqrt(easedNow);
			_birdsEyePhase = EBirdsEyePhase.FlyDown;
			_birdsEyeElapsed = (1f - startT) * duration;
		}
	}

	// Per-frame camera drive while bird's-eye view is active. Owns position
	// (lifted along world-up off the player), ortho Size (zoom), and the
	// motion-blur uniform consumed by UpdatePostProcess. End-of-FlyDown signals
	// Player.OnBirdsEyeReturnComplete to drop the movement lock and restores
	// the normal follow-position so the next frame's standard camera path
	// resumes seamlessly.
	void UpdateBirdsEyeCamera(double deltaTime)
	{
		float dt = (float)deltaTime;
		_birdsEyeElapsed += dt;

		// Tick the Q/E rotation tween, rotation-blur decay, and clip-plane
		// fade every frame so CameraLeft / CameraRight stay responsive AND
		// the ceiling-cutaway dissolve runs to completion during the overlook.
		// The camera's own UpdateCamera (which normally runs these) is
		// skipped while bird's-eye owns the pose.
		camera.TickRotation(dt);
		camera.AdvanceClipFade(dt);

		// Re-sync the cloud quad's visibility against the `clouds` CVar each
		// frame so toggling it mid-overlook updates without waiting for the
		// next FlyUp/Down.
		if (_cloudOverheadPlane != null)
		{
			_cloudOverheadPlane.Visible = CVars.clouds.Value;
		}

		float duration = Mathf.Max(0.0001f, birdsEyeTransitionSeconds);
		float t;
		bool finished = false;
		if (_birdsEyePhase == EBirdsEyePhase.FlyUp)
		{
			t = Mathf.Min(1f, _birdsEyeElapsed / duration);
			if (t >= 1f)
			{
				_birdsEyePhase = EBirdsEyePhase.Steady;
			}
		}
		else if (_birdsEyePhase == EBirdsEyePhase.FlyDown)
		{
			t = 1f - Mathf.Min(1f, _birdsEyeElapsed / duration);
			if (t <= 0f)
			{
				finished = true;
			}
		}
		else
		{
			t = 1f;
		}

		// Ease-out in BOTH directions: the camera leaves fast and decelerates
		// into its destination — the apex on fly-up, the ground on fly-down.
		// A single curve of t would ease-out one way and ease-in the other
		// (t descends 1→0 on FlyDown), so the curve is picked per phase.
		// FlyDown's start elapsed is seeded in OnPlayerBirdsEye to keep the
		// eased height continuous across a mid-fly-up cancel.
		float eased = _birdsEyePhase == EBirdsEyePhase.FlyDown
			? t * t                       // ease-out toward the ground (t: 1→0)
			: 1f - (1f - t) * (1f - t);   // ease-out toward the apex  (t: 0→1)

		// Camera pose. Read live camera.Yaw (TickRotation just updated it) so
		// CameraLeft / CameraRight rotate the overview, lift straight up along
		// world-Y by the eased altitude, and steepen the pitch by the eased
		// delta so the overview looks further down. Horizontal tracking stays
		// glued to the player so a knockback (which bypasses the movement lock)
		// doesn't strand the view.
		float pitchDeg = Mathf.Lerp(camera.pitchDegrees, camera.pitchDegrees - birdsEyePitchDelta, eased);
		float pitch = Mathf.DegToRad(pitchDeg);
		camera.GlobalRotation = new Vector3(pitch, camera.Yaw, 0f);
		Vector3 baseOffset = camera.GlobalTransform.Basis.Z * camera.distance;
		Vector3 lifted = baseOffset + Vector3.Up * birdsEyeAltitude;
		// Anchor on the SAME framing target the normal follow uses, not the raw
		// player feet — otherwise the eased=0 endpoint sits ~followHeightOffset
		// below where the standard camera path resumes, popping the view on the
		// FlyDown handoff.
		Vector3 followTarget = camera.GetFollowTarget(_player.GlobalPosition);
		camera.GlobalPosition = followTarget + baseOffset.Lerp(lifted, eased);

		camera.Size = Mathf.Lerp(_birdsEyeBaseSize, _birdsEyeBaseSize * birdsEyeSizeMultiplier, eased);

		// Push fog visibility along the same eased curve so the overview clears
		// in step with the lift. SkyController reads this every frame; on
		// FlyDown completion we reset it to 1.0 below so ground-level fog
		// resumes its normal range. Null-safe: SkyController is created during
		// scene init and outlives GameClient, but the static singleton may
		// briefly be unset during teardown.
		if (SkyController.Current != null)
		{
			SkyController.Current.FogVisibilityScale = Mathf.Lerp(1f, birdsEyeFogVisibilityScale, eased);
		}

		// Push the cloud band's world-Y bounds to the 2D cloud shader.
		//
		// Apex band position (eased=1): [50%, 75%] of player→camera height.
		//
		// Start band position (eased=0): the entire band sits ABOVE the
		// camera's ortho frustum, so no ray crosses it and alpha=0. As
		// `eased` rises 0→1 we slide the band downward through the
		// (stationary) camera, which the shader's path-length integration
		// reads as the camera "rising through" the cloud — same fade-in
		// visual without actually lifting the camera node.
		if (_cloudOverheadMaterial != null)
		{
			float playerY = _player.GlobalPosition.Y;
			float apexCameraY = playerY + lifted.Y;
			float deltaY = apexCameraY - playerY;
			float thickness = 0.25f * deltaY;
			// Camera Y at eased=t — typically constant when birdsEyeAltitude=0.
			float currentCameraY = playerY + baseOffset.Lerp(lifted, eased).Y;
			// Ortho's world-Y extent above the optical axis (= half ortho
			// Size projected onto world-up via Basis.Y.Y). Plus a small
			// padding so the band is unambiguously above ALL view rays.
			float orthoBufferY = camera.Size * 0.5f * Mathf.Abs(camera.GlobalTransform.Basis.Y.Y) + 5f;
			float startBottom = currentCameraY + orthoBufferY;
			float startTop = startBottom + thickness;
			float targetBottom = playerY + 0.5f * deltaY;
			float targetTop = playerY + 0.75f * deltaY;
			float bandBottom = Mathf.Lerp(startBottom, targetBottom, eased);
			float bandTop = Mathf.Lerp(startTop, targetTop, eased);
			_cloudOverheadMaterial.SetShaderParameter("band_bottom_altitude", bandBottom);
			_cloudOverheadMaterial.SetShaderParameter("band_top_altitude", bandTop);
		}

		// Motion blur fires only during FlyUp — peaks at mid-flight via sin(πt)
		// so it builds with acceleration and is gone by the time the camera
		// settles at the apex. Steady and FlyDown render clean.
		_birdsEyeBlur = _birdsEyePhase == EBirdsEyePhase.FlyUp ? Mathf.Sin(t * Mathf.Pi) * birdsEyeMotionBlurPeak : 0f;

		if (finished)
		{
			_birdsEyePhase = EBirdsEyePhase.None;
			_birdsEyeBlur = 0f;
			camera.Size = _birdsEyeBaseSize;
			camera.ManualClipMode = false;
			if (SkyController.Current != null)
			{
				SkyController.Current.FogVisibilityScale = 1f;
				SkyController.Current.CloudAltitudeOverride = null;
			}
			// Re-seat the follow position so the normal camera path picks up
			// from the player on the next frame rather than lerping from the
			// stale (lifted) follow target.
			camera.SetInitialPosition(_player.GlobalPosition);
			if (_cloudOverheadPlane != null)
			{
				_cloudOverheadPlane.Visible = false;
			}
			_player.OnBirdsEyeReturnComplete();
		}
	}

	// Pushes `sprite_chunky` to the pre-zoom base value so sprite-billboard
	// world size doesn't track the inflated bird's-eye ortho Size. Must run
	// AFTER SnapCameraAndUpdateUpscale (which sets the live-Size value); the
	// _Process bird's-eye branch calls it in that order. Skipped (no-op) when
	// the viewport isn't yet wired so the first-frame init path is safe.
	void ApplyBirdsEyeSpriteChunky()
	{
		if (sceneViewport == null)
		{
			return;
		}
		float baseChunky = _birdsEyeBaseSize / Mathf.Max(1, sceneViewport.Size.Y);
		RenderingServer.GlobalShaderParameterSet("sprite_chunky", baseChunky);
	}

	void UpdateViewportSize()
	{
		if (sceneViewport == null)
		{
			return;
		}
		Vector2I screenSize = GetTree().Root.Size;
		int scale = Math.Max(1, CVars.pixelScale.Value);
		// +1 pixel padding on each axis for subpixel camera offset.
		int innerW = (screenSize.X + scale - 1) / scale + 1;
		int innerH = (screenSize.Y + scale - 1) / scale + 1;
		sceneViewport.Size = new Vector2I(innerW, innerH);

		if (upscaleMaterial != null)
		{
			Vector2 uvScale = new Vector2(
				(float)screenSize.X / (scale * innerW),
				(float)screenSize.Y / (scale * innerH));
			upscaleMaterial.SetShaderParameter("uv_scale", uvScale);
		}
	}

	void SnapCameraAndUpdateUpscale()
	{
		if (sceneViewport == null || upscaleMaterial == null)
		{
			return;
		}

		int scale = Math.Max(1, CVars.pixelScale.Value);
		Vector2I screenSize = GetTree().Root.Size;
		Vector2I innerSize = sceneViewport.Size;

		// World units per inner-viewport texel. Orthographic camera.Size is
		// the vertical world extent mapped across innerSize.Y texels (Godot
		// derives horizontal size from viewport aspect, so texel width in
		// world equals this too). The camera must snap in multiples of this
		// so every voxel edge projects to the same sub-texel offset frame
		// to frame — otherwise wall pixels crawl within each chunky block.
		float chunky = camera.Size / Mathf.Max(1, innerSize.Y);
		RenderingServer.GlobalShaderParameterSet("sprite_chunky", chunky);

		// Vertical stretch = 1/cos(camera pitch) — compensates for the main
		// camera's tilt so one source pixel = one screen pixel. The shadow
		// caster uses the same stretch to match the visible sprite's
		// world-space height, keeping shadow length consistent with the view.
		// Vertical stretch = 1/cos(camera pitch) — compensates for the main
		// camera's tilt so one source pixel = one screen pixel.
		Vector3 mainForward = camera.GlobalBasis.Z;
		float mainPitch = Mathf.Asin(Mathf.Clamp(Mathf.Abs(mainForward.Y), 0f, 1f));
		float spriteStretch = 1f / Mathf.Max(Mathf.Cos(mainPitch), 1e-4f);
		RenderingServer.GlobalShaderParameterSet("sprite_stretch", spriteStretch);
		// Flat-on-ground sprite stretch = 1/sin(camera pitch). Read by the
		// sprite_lit_flat shader. The depth axis (horizontal, away from
		// camera) projects to screen Y with sin(pitch); inverting that
		// recovers a 1:1 source-pixel-to-screen-pixel mapping for flat
		// sprites just like sprite_stretch does for upright. Behaves
		// reciprocally to spriteStretch — high-pitch (camera near vertical)
		// stretches upright sprites toward infinity but leaves flat sprites
		// at ~1, and vice versa.
		float spriteStretchFlat = 1f / Mathf.Max(Mathf.Sin(mainPitch), 1e-4f);
		RenderingServer.GlobalShaderParameterSet("sprite_stretch_flat", spriteStretchFlat);

		Vector3 pos = camera.GlobalPosition;
		Basis basis = camera.GlobalBasis;
		Vector3 right = basis.X;
		Vector3 up = basis.Y;
		Vector3 forward = basis.Z;

		float rx = right.Dot(pos);
		float ry = up.Dot(pos);
		float rz = forward.Dot(pos);

		float sx = Mathf.Floor(rx / chunky) * chunky;
		float sy = Mathf.Floor(ry / chunky) * chunky;
		float fracX = rx - sx;
		float fracY = ry - sy;

		camera.GlobalPosition = sx * right + sy * up + rz * forward;

		// fracX/fracY in [0, chunky); convert to texel units (in [0,1) of a
		// single inner texel) and then to UV.
		float texFracX = fracX / chunky;
		float texFracY = fracY / chunky;
		Vector2 uvOffset = new Vector2(texFracX / innerSize.X, texFracY / innerSize.Y);
		_subpixelTexelOffset = new Vector2(texFracX, texFracY);

		upscaleMaterial.SetShaderParameter("uv_offset", uvOffset);
		// uv_scale may drift if pixel_scale is changed at runtime without a
		// window resize; refresh it every frame so the CVar toggle works live.
		Vector2 uvScale = new Vector2(
			(float)screenSize.X / (scale * innerSize.X),
			(float)screenSize.Y / (scale * innerSize.Y));
		upscaleMaterial.SetShaderParameter("uv_scale", uvScale);

		if (sceneViewport.Size.X != innerSize.X || sceneViewport.Size.Y != innerSize.Y)
		{
			UpdateViewportSize();
		}
	}

	// Red damage-flash intensity in [0, 1]. Bumped by FlashDamage on every
	// player hit (direct + DOT rollup), decayed linearly each frame so the
	// flash fades over damageFlashFadeSeconds.
	float _damageFlash;

	// Heartbeat pulse state. `_heartbeatPhase` is the position in the current
	// lub-dub cycle in [0, 1); a cycle boundary retriggers the SFX. While the
	// player is alive the rate tracks the low-health ramp; on death we latch
	// the live rate and ease it (and the pulse amplitude) to zero over the
	// death-slowdown window, so the thump-thump audibly winds down.
	float _heartbeatPhase;
	bool _heartbeatActive;
	float _heartbeatLiveRate;
	bool _heartbeatDying;
	float _heartbeatDeathElapsed;
	float _heartbeatDeathStartRate;
	float _heartbeatDeathSlowdown;

	// Counts down from lowHealthEffectSeconds; refilled on every hit. The
	// normalized value (eased) is the master multiplier on the whole
	// low-health overlay, so it fades out a few seconds after the last hit.
	float _lowHealthEffectTimer;

	// Lub-dub envelope shape, in cycle-phase units. The lub sits at phase 0
	// (cycle boundary, where the SFX fires); the quieter dub follows shortly
	// after. Each is a smooth cosine bump of the given half-width.
	const float HEARTBEAT_LUB_WIDTH = 0.07f;
	const float HEARTBEAT_DUB_OFFSET = 0.2f;
	const float HEARTBEAT_DUB_WIDTH = 0.06f;
	const float HEARTBEAT_DUB_STRENGTH = 0.65f;
	// Pitch floor the slowing heartbeat sags toward as it dies out.
	const float HEARTBEAT_DEATH_PITCH = 0.7f;
	// Bus-relative volume floor the dying heartbeat fades toward.
	const float HEARTBEAT_DEATH_VOLUME_DB = -30f;

	// Bumps the damage flash by the hit fraction of max health, scaled by
	// damageFlashScale and capped at 1. Stacks with whatever is already in
	// the buffer (max-of) so a follow-up hit during a fade doesn't shrink
	// the flash. Called from Player.OnHurtBoxHit (direct) and from
	// _PhysicsProcess after each DOT HUD flush.
	public void FlashDamage(float amount)
	{
		if (amount <= 0f || _player == null) { return; }
		float maxHealth = _player.MaxHealth;
		if (maxHealth <= 0f) { return; }
		// Any hit refills the low-health overlay window — the effect snaps back
		// to full and resumes its nearness-to-death intensity (the ramp is
		// recomputed live from current health).
		_lowHealthEffectTimer = lowHealthEffectSeconds;
		float intensity = Mathf.Clamp(amount / maxHealth * damageFlashScale, 0f, 1f);
		if (intensity > _damageFlash)
		{
			_damageFlash = intensity;
		}
	}

	void UpdatePostProcess()
	{
		if (postProcessMaterial == null) { return; }

		postProcessMaterial.SetShaderParameter("vignette_radius", CVars.vignetteRadius.Value);
		postProcessMaterial.SetShaderParameter("vignette_softness", CVars.vignetteSoftness.Value);
		postProcessMaterial.SetShaderParameter("vignette_strength", CVars.vignetteStrength.Value);

		// Motion blur — combined max-of between the camera's rotation blur
		// (decays over rotationBlurDuration after a Q/E press) and the
		// bird's-eye fly-up blur. The CVar gates only the rotation source so
		// the bird's-eye effect runs even when rotation blur is disabled.
		// When `motion_blur_strength` is 0 the shader skips the blur loop, so
		// idle frames pay nothing.
		float rotBlur = CVars.rotationBlur.Value ? camera.RotationBlurStrength : 0f;
		float blurStrength = Mathf.Max(rotBlur, _birdsEyeBlur);
		Vector2 blurDir = _birdsEyeBlur > rotBlur ? BIRDS_EYE_BLUR_DIR : camera.RotationBlurDir;
		postProcessMaterial.SetShaderParameter("motion_blur_strength", blurStrength);
		postProcessMaterial.SetShaderParameter("motion_blur_dir", blurDir);

		// Decay the flash. dt comes from the engine's _Process delta — we
		// don't have it here directly, so pull from the frame time. This
		// runs once per visual frame (called from _Process), so
		// GetProcessDeltaTime is the right scale.
		float dt = (float)GetProcessDeltaTime();
		if (_damageFlash > 0f && damageFlashFadeSeconds > 0f)
		{
			_damageFlash = Mathf.Max(0f, _damageFlash - dt / damageFlashFadeSeconds);
		}
		postProcessMaterial.SetShaderParameter("damage_flash", _damageFlash);
		postProcessMaterial.SetShaderParameter("damage_flash_color", damageFlashColor);
		// SetShaderParameter accepts a null Texture2D — the shader's
		// hint_default_white kicks in and the flash paints uniformly red.
		postProcessMaterial.SetShaderParameter("damage_flash_tex", damageFlashTexture);

		// Low-health overlay. Ramp = (threshold - healthFrac) / threshold
		// so at threshold the ramp is 0, at 0 health the ramp is 1. Each
		// component (desat, dim) is sent pre-scaled by its max so the
		// shader just applies a 0..1.
		float ramp = 0f;
		if (_player != null && lowHealthThreshold > 0f)
		{
			float maxHealth = _player.MaxHealth;
			if (maxHealth > 0f)
			{
				float frac = Mathf.Clamp(_player.Health / maxHealth, 0f, 1f);
				ramp = Mathf.Clamp((lowHealthThreshold - frac) / lowHealthThreshold, 0f, 1f);
			}
		}
		// Damage-gated fade. The overlay only lingers for a window after the
		// last hit (refilled in FlashDamage); past that it eases out. Eased
		// with smoothstep so it holds near full for most of the window and
		// drops off toward the end rather than dimming the whole time.
		_lowHealthEffectTimer = Mathf.Max(0f, _lowHealthEffectTimer - dt);
		float fade = lowHealthEffectSeconds > 0f
			? Mathf.SmoothStep(0f, 1f, _lowHealthEffectTimer / lowHealthEffectSeconds)
			: 0f;

		// Heartbeat thump. Active whenever the overlay is showing or the death
		// wind-down is still running. `pulse` is the lub-dub envelope scaled by
		// the death amplitude AND the damage-gated fade; it breathes the
		// desaturation (color bleeds back) and feeds the shader's red-tint
		// surge. The cadence still tracks nearness to death — only the
		// amplitude/volume fades with the window.
		float pulse = UpdateHeartbeat(dt, ramp, fade);
		float baseDesat = ramp * lowHealthMaxDesaturation * fade;
		float desat = baseDesat * (1f - pulse * lowHealthHeartbeatDesaturationPulse);
		postProcessMaterial.SetShaderParameter("low_health_desaturation", desat);
		postProcessMaterial.SetShaderParameter("low_health_dim", ramp * lowHealthMaxDim * fade);
		postProcessMaterial.SetShaderParameter("low_health_pulse", pulse * lowHealthHeartbeatRedTint);
		postProcessMaterial.SetShaderParameter("low_health_pulse_color",
			new Vector3(lowHealthHeartbeatColor.R, lowHealthHeartbeatColor.G, lowHealthHeartbeatColor.B));
	}

	// Advances the heartbeat phase and returns the current lub-dub envelope
	// value in [0, 1] (already scaled by the death-wind-down amplitude and the
	// damage-gated `fade`). The heartbeat is live while `ramp` > 0 and the fade
	// window is open; on death it ignores both and decelerates the latched rate
	// to a stop. Retriggers the SFX on each cycle boundary. Returns 0 when idle.
	float UpdateHeartbeat(float dt, float ramp, float fade)
	{
		bool active = (ramp > 0f && fade > 0f) || _heartbeatDying;
		if (!active)
		{
			_heartbeatActive = false;
			return 0f;
		}

		// Per-frame rate (cycles/sec) and the amplitude/pitch envelope.
		float rate;
		float amplitude;
		float pitch;
		if (_heartbeatDying)
		{
			_heartbeatDeathElapsed += dt;
			float t = _heartbeatDeathSlowdown > 0f
				? Mathf.Clamp(_heartbeatDeathElapsed / _heartbeatDeathSlowdown, 0f, 1f)
				: 1f;
			// Ease-out so the deceleration is steep at first then crawls to a
			// halt — reads as a heart giving out rather than a linear ramp.
			float ease = 1f - (t * t);
			rate = _heartbeatDeathStartRate * ease;
			amplitude = ease;
			pitch = Mathf.Lerp(HEARTBEAT_DEATH_PITCH, 1f, ease);
		}
		else
		{
			float bpm = Mathf.Lerp(lowHealthHeartbeatSlowBpm, lowHealthHeartbeatFastBpm, ramp);
			rate = bpm / 60f;
			_heartbeatLiveRate = rate;
			amplitude = 1f;
			pitch = Mathf.Lerp(1f, lowHealthHeartbeatMaxPitch, ramp);
		}
		// The damage-gated window fades the heartbeat's loudness/strength
		// without touching its cadence. Death refills the window, so the
		// wind-down always plays at full.
		amplitude *= fade;

		// Fire the first beat the instant the overlay engages, then on every
		// cycle wrap. New beats stop once the dying rate has crawled to zero.
		bool beat = false;
		if (!_heartbeatActive)
		{
			_heartbeatActive = true;
			_heartbeatPhase = 0f;
			beat = true;
		}
		else
		{
			_heartbeatPhase += rate * dt;
			if (_heartbeatPhase >= 1f)
			{
				_heartbeatPhase -= Mathf.Floor(_heartbeatPhase);
				beat = true;
			}
		}

		if (beat && heartbeatAudio != null && amplitude > 0.02f)
		{
			heartbeatAudio.PitchScale = pitch;
			heartbeatAudio.VolumeDb = Mathf.Lerp(HEARTBEAT_DEATH_VOLUME_DB, lowHealthHeartbeatVolumeDb, amplitude);
			heartbeatAudio.Play();
		}

		return HeartbeatEnvelope(_heartbeatPhase) * amplitude;
	}

	// Two smooth cosine bumps per cycle — the loud lub at the boundary and a
	// softer dub just after — forming the thump-thump shape.
	static float HeartbeatEnvelope(float phase)
	{
		float lub = HeartbeatBump(phase, 0f, HEARTBEAT_LUB_WIDTH);
		float dub = HeartbeatBump(phase, HEARTBEAT_DUB_OFFSET, HEARTBEAT_DUB_WIDTH) * HEARTBEAT_DUB_STRENGTH;
		return Mathf.Max(lub, dub);
	}

	// Cosine bump centered at `center` (cycle-wrapped) with the given
	// half-width: 1 at the center, smoothly to 0 at ±width, 0 beyond.
	static float HeartbeatBump(float phase, float center, float width)
	{
		float d = Mathf.Abs(phase - center);
		d = Mathf.Min(d, 1f - d);
		if (d >= width)
		{
			return 0f;
		}
		return 0.5f * (1f + Mathf.Cos(d / width * Mathf.Pi));
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
			if (CVars.debugFlyCam.Value && Input.IsMouseButtonPressed(MouseButton.Right))
			{
				_flyYaw -= mouseMotion.Relative.X * FLYCAM_LOOK_SENSITIVITY;
				_flyPitch -= mouseMotion.Relative.Y * FLYCAM_LOOK_SENSITIVITY;
				_flyPitch = Mathf.Clamp(_flyPitch, -Mathf.Pi / 2f + 0.01f, Mathf.Pi / 2f - 0.01f);
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
			if (_mousePosition.LengthSquared() > AIM_CURSOR_RADIUS_PX * AIM_CURSOR_RADIUS_PX)
			{
				_mousePosition = _mousePosition.Normalized() * AIM_CURSOR_RADIUS_PX;
			}
			if (_mousePosition.LengthSquared() >= AIM_CURSOR_DEADZONE_PX * AIM_CURSOR_DEADZONE_PX)
			{
				// Pass the deflection normalized to the disk radius so the
				// magnitude matches the gamepad right-stick convention (0..1).
				// Positional aim integrates this as a rate input; Directional
				// reads only the angle so it doesn't care either way.
				Vector2 deflection01 = _mousePosition / AIM_CURSOR_RADIUS_PX;
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
		IInteractive target = _player?.CurInteractive ?? _player?.HighlightInteractive;
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

	// Player.onDied bridge. Suppress gameplay input for the entire death
	// sequence (fade-out → prompt → fade-in); DeathScreen clears the gate
	// at the end of its fade-in. Notify subscribers, then hand control to
	// the DeathScreen for the visual + audio sequence.
	void OnPlayerDiedInternal(Player player)
	{
		InputSuppressed = true;

		// Hand the heartbeat over to its death wind-down: latch the live rate
		// (fall back to the fast-BPM rate if the player died before the
		// overlay was ramping, e.g. a one-shot kill) and let UpdateHeartbeat
		// decelerate it to a stop. Sync the slowdown to the DeathScreen fade
		// so the heart and the screen go quiet together.
		_heartbeatDying = true;
		_heartbeatDeathElapsed = 0f;
		// Refill the window so the death wind-down is always at full strength,
		// even if the killing blow landed after the overlay had faded out.
		_lowHealthEffectTimer = lowHealthEffectSeconds;
		_heartbeatDeathStartRate = _heartbeatLiveRate > 0f ? _heartbeatLiveRate : lowHealthHeartbeatFastBpm / 60f;
		_heartbeatDeathSlowdown = deathScreen != null && deathScreen.fadeOutSeconds > 0f
			? deathScreen.fadeOutSeconds
			: lowHealthDeathSlowdownSeconds;

		onPlayerDied?.Invoke(player);
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

		// Clear the death wind-down so the heartbeat goes fully idle (health is
		// restored, so the overlay ramp is 0); a fresh low-health episode will
		// re-engage it from scratch.
		_heartbeatDying = false;
		_heartbeatActive = false;
		_heartbeatDeathElapsed = 0f;
		_lowHealthEffectTimer = 0f;
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
