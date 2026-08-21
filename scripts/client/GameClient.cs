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
		{ EStatName.CritChance, "Crit Chance" },
		{ EStatName.ArmorPenetration, "Armor Penetration" },
		{ EStatName.Blunt, "Blunt" },
		{ EStatName.Dizzy, "Dizzy" },
		{ EStatName.Knockback, "Knockback" },
		{ EStatName.Block, "Block" },
		{ EStatName.Parry, "Parry" },
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
		{ EStatName.DamageScale, "Damage & Buildup" },
		{ EStatName.DamageReduction, "Damage Reduction" },
		{ EStatName.AnimSpeed, "Animation Speed" },
		{ EStatName.FootprintAlpha, "Footprint Alpha" },
		{ EStatName.FootprintDuration, "Footprint Duration" },
		{ EStatName.Fortitude, "Fortitude" },
		{ EStatName.Strength, "Strength" },
		{ EStatName.Perception, "Perception" },
		{ EStatName.Stealth, "Stealth" },
	};

	// Damage modifier trigger labels. Used as the header of the conditional
	// damage panels under each weapon action ("Crit" / "Dizzy" / "Backstab").
	public readonly Dictionary<EDamageTrigger, string> damageTriggerLabels = new Dictionary<EDamageTrigger, string>
	{
		{ EDamageTrigger.OnCrit, "Crit" },
		{ EDamageTrigger.OnDizzy, "Dizzy" },
		{ EDamageTrigger.OnBackstab, "Backstab" },
	};

	// Shared per-level "difficulty tell" tint ramp. Each species maps this to its
	// own accent mesh(es) (MobData.levelColorMeshNames — a spider's eyes, a drake's
	// eyes, a goblin's armor) so a mob's level reads at a glance and consistently
	// across every species. Indexed by MobSimState.Level (0..4, matching the HUD
	// level pips); MobLevelColor clamps out-of-range levels to the nearest end.
	// Authored here as the single source of truth; still inspector-tunable per the
	// [Export] convention. Escalates neutral → yellow → orange → red → violet.
	[Export] public Color[] mobLevelColors =
	{
		new Color(0.85f, 0.85f, 0.85f),
		new Color(1f, 0.82f, 0.25f),
		new Color(1f, 0.5f, 0.12f),
		new Color(0.95f, 0.15f, 0.12f),
		new Color(0.7f, 0.2f, 1f),
	};

	// The shared level tint for a mob at the given level (see mobLevelColors).
	// Static + null-safe so the spawn path can call it before/without a live
	// GameClient (editor, tests) — falls back to white (an inert recolor).
	public static Color MobLevelColor(int level)
	{
		Color[] colors = Current?.mobLevelColors;
		if (colors == null || colors.Length == 0)
		{
			return Colors.White;
		}
		return colors[Mathf.Clamp(level, 0, colors.Length - 1)];
	}

	[ExportGroup("Grounding Shadows")]
	// Airborne casters — fliers and shadow-casting projectiles — drop their
	// grounding-shadow blob straight onto the terrain below (not at body height)
	// and always keep it on regardless of daylight (their real directional
	// shadow lands off to the side, so the blob is the only cue for where they
	// hover). The blob also grows and softens with height, reading like a real
	// contact shadow spreading and fading with distance: at airShadowReferenceHeight
	// meters up it reaches airShadowMaxGrowth× its ground diameter and
	// airShadowMinAlpha× its ground alpha, lerping linearly from the on-ground
	// values and clamping past the reference. At height 0 it's identical to a
	// grounded blob. (The blob material / darkness / daylight-fade knobs live on
	// SimData; these are purely the airborne presentation.)
	[Export(PropertyHint.Range, "1,40,0.5")] public float airShadowReferenceHeight = 12f;
	[Export(PropertyHint.Range, "1,5,0.05")] public float airShadowMaxGrowth = 2.5f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float airShadowMinAlpha = 0.35f;
	[ExportGroup("")]

	[Export] public GameCamera camera;
	// Debug free-fly camera (WASD + right-drag), gated by the `debugFlyCam`
	// CVar. GameClient ticks it in _Process and forwards mouse-motion in _Input.
	[Export] public FlyCamera flyCamera;
	[Export] public Hud hud;
	[Export] public AlmanacScreen almanacScreen;
	[Export] public MerchantScreen merchantScreen;
	[Export] public CampScreen campScreen;
	[Export] public DeathScreen deathScreen;
	[Export] public SleepOverlay sleepOverlay;
	// Full-screen black fade for the cinematic camp entry (lighting a campfire).
	[Export] public ScreenFade campFade;
	[Export] public UpgradeScreen upgradeScreen;
	[Export] public ForgeScreen forgeScreen;
	[Export] public Node worldHUD;
	// Pool that leases MobHUDs to the mobs currently showing one, instead of
	// every loaded mob owning one. Created in StartWorld under worldHUD.
	private MobHudManager _mobHuds;
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
	// Climb/mantle prompt for the context button. Spawned off Player.TraversalPreview
	// rather than off a highlight, since a ledge is not an interactive.
	[Export] public PackedScene climbHudScene;
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
	[Export] public PackedScene hudTextMissScene;
	[Export] public PackedScene hudTextBlockedScene;
	[Export] public PackedScene hudTextParriedScene;
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
	[ExportGroup("Mouse Aim")]
	// Directional-aim saturation radius (pixels) for the virtual aim-stick. ONLY
	// affects Directional aim: aim direction is atan2(disk), magnitude-independent,
	// so the radius purely sets how far the mouse travels before the deflection
	// pins at the disk edge — i.e. the reversal/sweep feel. Smaller = snappier
	// (a flick to the opposite heading unwinds less). Kept range-INDEPENDENT so
	// every weapon steers with the same wrist motion. Positional aim no longer
	// reads this — it integrates a world-unit motion delta instead (see
	// mousePositionalMetersPerPixel).
	[Export(PropertyHint.Range, "20,1200,1")] public float mouseDirectionalDiskRadiusPx = 250f;
	// Positional-aim sensitivity: world meters the ground cursor travels per pixel
	// of raw mouse motion (× mouse_sensitivity). Range-INDEPENDENT so the cursor
	// "feels like a screen cursor" regardless of weapon reach — range only clamps
	// the result to its disk. Gamepad ignores this (it's a range-relative rate).
	[Export(PropertyHint.Range, "0.002,0.1,0.002")] public float mousePositionalMetersPerPixel = 0.02f;
	// Below this magnitude the accumulator is treated as "at rest" and the
	// player's aim FACING is left alone (Directional only). Stops sub-pixel jitter
	// from continuously re-aiming near disk-center, where the angle is ill-defined.
	// Does not gate the positional delta — that integrates from raw motion.
	[Export(PropertyHint.Range, "0,50,0.5")] public float mouseDirectionalDeadzonePx = 5f;

	[ExportGroup("Subsystems")]
	// Authored as embedded child scenes in game.tscn — their tuning lives on
	// the Minimap / HeatField nodes, not here. Sim references and initializes
	// them rather than creating them.
	[Export] public Minimap minimap;
	[Export] public HeatField heatField;

	[ExportGroup("Foliage Player Fade")]
	// Cutaway tube radius around the camera→player capsule axis. The
	// effective radius pushed to the shader lerps between 0 (no cutaway)
	// and this value based on whether the CPU probe
	// (Sim.IsFadeVolumeOccluded) finds any fade-eligible cluster on the
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

	[ExportGroup("Ceiling Cutaway")]
	// Samples per ring, and how many rings fill the current reach. Together they
	// set how finely the ring can resolve the edge of a covered area.
	[Export(PropertyHint.Range, "4,32,1")] public int clipIrisRingSamples = 12;
	[Export(PropertyHint.Range, "1,6,1")] public int clipIrisRingCount = 3;
	// The two iris sizes, in metres — the small disk while the player is visible
	// and the large one while they are hidden. They also set how far the probe
	// ring reaches, since it only ever needs to see as far as the disk could grow.
	//
	// These are the shape's SHORT axis: the foliage cutaway's aspect scales them out
	// to an ellipse (1.6 wide, 1.2 tall at the defaults), so the disk on screen is
	// wider than the number here.
	[Export(PropertyHint.Range, "1,16,0.25")] public float clipIrisRadiusMin = 3.5f;
	// The reach is not just detection range — the disk grows to the farthest OCCLUDED
	// sample, so a ring reaching the next building along latches on it and then removes
	// everything between here and there. Keep it near the space the player is actually
	// walking into.
	[Export(PropertyHint.Range, "2,32,0.25")] public float clipIrisRadiusMax = 8f;
	// What a doorway or window peek opens to on its own, halfway between the two by
	// default. Its own number because a peek is neither of the other cases: nothing is
	// hiding the player, so the ring finds no occlusion to size a disk from, yet a room
	// seen through an opening is worth more than the size that means "nothing is wrong
	// here". Clamped into the pair, so it can never undercut the small size.
	[Export(PropertyHint.Range, "1,32,0.25")] public float clipIrisOpeningRadius = 5.75f;
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float clipIrisRangeSeconds = 0.4f;
	// Height above a sample's own floor that the occlusion march starts from, so
	// the query asks whether the SPACE is hidden rather than whether the ground is.
	[Export(PropertyHint.Range, "0.25,3,0.05")] public float clipIrisBodyHeight = 1f;
	// How far up a sample looks for a ceiling before calling it sky. Has to reach a
	// roof, not the sky.
	[Export(PropertyHint.Range, "4,48,1")] public int clipIrisCeilingScan = 24;
	// Voxels of vertical slack a sample gets when finding its own floor. Below
	// this, stepped ground would read as a wall on every side; above it, a step the
	// player would actually have to climb starts reading as walkable space.
	[Export(PropertyHint.Range, "0,4,1")] public int clipIrisFloorTolerance = 2;
	// How far along the camera ray a sample looks for something hiding it. Past
	// this the occluder is off-screen anyway.
	[Export(PropertyHint.Range, "4,64,1")] public float clipIrisOcclusionDistance = 24f;
	// The two heights the occlusion ray starts from, and the elevation that decides
	// between them. The LOW raise is asked first, so cover standing right beside the
	// player still registers; its answer is believed whenever the thing that blocked
	// it stands taller than clipIrisShortCover above the player's floor, because that
	// is a building. Only a SHORT blocker gets the question re-asked from the HIGH
	// raise, where a terrace passes under the ray and reports clear while a building
	// keeps blocking.
	//
	// One raise alone cannot do both: high enough to ignore a terrace is also high
	// enough to ignore a wall you are standing against.
	[Export(PropertyHint.Range, "0.5,8,0.25")] public float clipIrisOcclusionLift = 2f;
	[Export(PropertyHint.Range, "1,12,0.25")] public float clipIrisOcclusionLiftHigh = 4.5f;
	// Wants to sit just above a PLATEAU_STEP, so one terrace reads as short and two
	// do not.
	[Export(PropertyHint.Range, "1,12,0.25")] public float clipIrisShortCover = 4.25f;
	// Metres each rung of the player-hidden ladder rises above the one below, starting
	// at the eye. Three rays are cast to the camera and the share of them that come
	// back blocked eases the reach from its small size to its large one — so being
	// half behind a wall reads as a partial reveal instead of snapping between two
	// sizes the instant an edge crosses the eye.
	[Export(PropertyHint.Range, "0.25,3,0.25")] public float clipIrisHiddenRise = 1f;
	// How far below the surface overhead a cut plane parks. Too small and the one
	// face you can see from beneath survives, so the cutaway reads as having done
	// nothing.
	//
	// THE one clearance: it sets the base plane (under the voted ceiling), the disk's
	// plane, and the camera's manual reveal (under the plateau) alike, so changing it
	// moves all three together instead of leaving them cutting at different heights
	// over the same floor.
	[Export(PropertyHint.Range, "0.1,1,0.05")] public float clipClearance = 0.5f;
	// Metres from the player within which a window or door does NOT stop the probe
	// ring. Openings otherwise block it — a hole is not cover to anything else in the
	// cutaway, so the ring poured through every window in sight and sampled the room
	// beyond. Standing IN a doorway or right against a window has to keep working
	// though; that is the moment the reveal exists for. Wants to stay at about "in it
	// or touching it" — grow this and distant windows start latching the disk again.
	[Export(PropertyHint.Range, "0,4,0.25")] public float clipIrisOpeningReach = 1.5f;
	// Metres of margin past the farthest hidden sample, so the reveal clears the
	// thing doing the hiding rather than stopping on it.
	[Export(PropertyHint.Range, "0,8,0.25")] public float clipIrisPadding = 2f;
	// Metres of DITHERED ramp at the disk's edge — the same stipple the height
	// transition uses, so the two read as one effect. Near zero gives a hard edge.
	//
	// Measured on the SHORT axis, like the radius: the shape divides by the aspect, so
	// the ramp is proportionally wider along the horizontal than the number suggests.
	[Export(PropertyHint.Range, "0.05,4,0.05")] public float clipIrisEdgeSoftness = 0.5f;
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float clipIrisGrowSeconds = 0.35f;
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float clipIrisShrinkSeconds = 0.5f;
	// Time constant the disk's PLANE eases toward its height over. That height is
	// derived from clipClearance rather than authored here — see ClipIris — so the
	// disk and the base plane cannot be tuned apart.
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float clipIrisHeightSeconds = 0.25f;
		// Temporal hysteresis on the disk's open gate: it stays open at least this
		// long after the player stops being hidden or leaves an opening, so the
		// quantised hidden-ladder ticking across zero at a wall edge cannot make it
		// oscillate. Only delays CLOSING; opening stays immediate.
		[Export(PropertyHint.Range, "0,1,0.05")] public float clipIrisHoldSeconds = 0.25f;

	[ExportGroup("")]

	public Action<Player> onPlayerSpawned;
	// Fired from OnPlayerDiedInternal (GameClient's own player.onDied bridge),
	// so subscribers get death reliably without racing the deferred player
	// spawn the way subscribing to the player directly would.
	public Action<Player> onPlayerDied;
	// Fired by RespawnPlayer after a death respawn (distinct from onPlayerSpawned,
	// which fires once on the initial spawn — the player object is reused on
	// respawn, so re-running onPlayerSpawned subscribers would double-bind). Music
	// uses it to leave the death track for the current time-of-day ambient.
	public Action<Player> onPlayerRespawned;
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
	// or language. The downstream discovery events on SimState /
	// Player still fire — only the visual announcement is suppressed.
	public bool SuppressAnnouncements;
	public void Announce(Announcement a)
	{
		if (a == null || SuppressAnnouncements) { return; }
		onAnnouncement?.Invoke(a);
	}

	// Fired the moment a mob's Die() runs, with the per-instance
	// DamagedByPlayer flag piped through so subscribers can decide whether
	// the player earned credit (bestiary discovery, future quest counters).
	// GameClient subscribes its own bestiary bridge in Init.
	public Action<SpeciesData, bool> onMobKilled;
	public void NotifyMobKilled(SpeciesData species, bool damagedByPlayer)
	{
		if (species == null) { return; }
		onMobKilled?.Invoke(species, damagedByPlayer);
	}

	// Player combat state, aggregated by CombatTracker from per-mob reports:
	// combat is on while a dangerous, player-perceived enemy is in an attack
	// behavior, lingers combatExitGraceSeconds after the player runs away, and
	// ends immediately when the last perceived threat is killed. These edge
	// events are the seam for music and any other in-combat reaction.
	public CombatTracker Combat { get; private set; }
	public Action onCombatBegin;
	public Action onCombatEnd;

	// Controller rumble driver — haptic sibling of the camera's Shake. Trigger
	// via Rumble.AddImpulse(...) or a ControllerRumble ItemEvent. Ticked in
	// _Process below.
	private readonly ControllerRumble _rumble = new();
	public ControllerRumble Rumble => _rumble;
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
	// Sim.Tick keeps running regardless so the runner can still advance a
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
	public Sim Sim => _world;

	Player _player;
	// The party's Player nodes, index-aligned with SimState.Party.Members.
	// One is active (== _player, controlled); the rest are inactive members that
	// idle where placed around camp. Populated by SpawnParty; used by
	// SwitchControlTo to move control between members.
	readonly List<Player> _partyPlayers = new();
	// Radius (m) of the ring that inactive party members spread around the
	// spawn / campfire anchor.
	[Export] float partyRingRadius = 2.5f;
	Sim _world;
	// Held from Init so party members recruited mid-run (RecruitToParty) can be
	// spawned as Player nodes the same way SpawnParty builds the starting roster.
	PackedScene _playerScene;
	WorldGenData _worldGenData;
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
	// The campfire the party is anchored to — the starting campfire until the
	// player camps somewhere new (CampScreen.Open → NotifyCampedAt). On a party
	// member's death the survivors gather here and the fade-in frames it.
	Vector3 _lastCampfirePosition;
	// The campfire the party camps / respawns at is always the world's single lit fire:
	// lighting one is the only way to camp, and it douses every other, so SimState.LitCampfire
	// is the one source of truth. Its RuntimeNode is repopulated on chunk reload and nulled on
	// unload, so — unlike a cached node ref — it never dangles. Null when nothing is lit or the
	// fire's chunk isn't resident yet. CampScreen reads this LIVE every frame rather than caching
	// a node, so cooking enables itself the moment a respawn/Pray fire streams in.
	public Campfire LitCampfireNode => _world?.WorldState?.SimState?.LitCampfire?.RuntimeNode as Campfire;
	Vector2 _mousePosition;
	Sprite3D _highlightOverlay;
	InteractHUD _interactHUD;
	ClimbHUD _climbHUD;

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

	// The member that just fell, held from OnPlayerDiedInternal until the death
	// blackout relocates its body; null outside that window.
	Player _fallenBody;

	// Wall-clock stamp for the post-process pass. The screen effects are
	// presentation, so they run on real time — the slow-mo death cam's
	// Engine.TimeScale must not stretch flash decays or the death heartbeat
	// (which is synced to the death-screen fade). The sim still gets the scaled
	// _Process delta via Sim.Tick.
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
		if (campScreen != null)
		{
			campScreen.Visible = false;
		}
		if (merchantScreen != null)
		{
			merchantScreen.Visible = false;
		}
		if (deathScreen != null)
		{
			deathScreen.Visible = false;
		}
		if (upgradeScreen != null)
		{
			upgradeScreen.Visible = false;
		}
		if (forgeScreen != null)
		{
			forgeScreen.Visible = false;
		}

		_inputSuppressed = false;
		_inputSuppressClearPending = false;

		// Tree-climb scout: when the gentle lift settles at its apex, open the
		// world map on the freshly-charted snapshot (see OnBirdsEyeLiftApex).
		if (birdsEye != null)
		{
			birdsEye.onLiftReachedApex += OnBirdsEyeLiftApex;
		}
	}

	public async void Init(Vector3 playerPosition, PackedScene playerScene, WorldGenData worldGenData, WorldState worldState, LoadingScreen loadingScreen = null)
	{
		_spawnPosition = playerPosition;
		_lastCampfirePosition = playerPosition;
		_playerScene = playerScene;
		_worldGenData = worldGenData;
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
		_world = new Sim();
		Combat = new CombatTracker(combatExitGraceSeconds);
		Combat.onCombatBegin = () => onCombatBegin?.Invoke();
		Combat.onCombatEnd = () =>
		{
			// The next peaceful encounter again requires the player to choose the
			// fight before a guard companion will attack (Player.CombatEngaged).
			_player?.ResetCombatEngaged();
			onCombatEnd?.Invoke();
		};
		Combat.onCombatVictory = OnCombatVictory;
		// Parented under worldHUD so the pooled HUDs inherit the same canvas and
		// pause behaviour they had when each mob owned one outright.
		_mobHuds = new MobHudManager();
		_mobHuds.Init(camera);
		worldHUD?.AddChild(_mobHuds);
		_world.onMobSpawned += OnMobSpawned;
		_world.onMobRemoved += OnMobRemoved;
		_world.onDiscoverableSpawned += OnDiscoverableSpawned;
		// Sim rolls the roster day (meal reset + well-rested lottery) inside
		// AdvanceToNextSunrise; the client only re-applies the per-member NODE effects.
		_world.OnNewDay += OnNewDayRefreshNodes;
		// Sim detects revive-deadline expiry and drops the roster entry; we free the
		// corpse node (those Player nodes are GameClient's).
		_world.onPartyMemberExpired += OnPartyMemberExpired;
		sceneViewport.AddChild(_world);
		// Sim.Initialize is the chunk-mesh sphere fill — fully synchronous
		// today (~900 chunks). The bar can't tick during this; it stays
		// frozen at 0.6 → 0.75 across the single hitch. Threading the
		// chunk fill (see voxels/CLAUDE.md) would make this smooth.
		_world.Initialize(worldState, playerPosition, camera, fogMaterial, () => _player?.GlobalPosition ?? playerPosition);
		// Bind this world's authored scripted content (quests) onto the runtime sim
		// state, now that Sim holds the WorldState (WorldGen and .hike-load paths alike).
		_world.BindScriptData(worldGenData?.scriptData);
		GD.Print($"[Load] Building world (chunk-mesh fill): {phaseSw.ElapsedMilliseconds}ms");
		phaseSw.Restart();
		loadingScreen?.SetProgress(0.75f, "Spawning...");

		// Bridge sim-side discovery events to the announcement bus. The
		// underlying SimState lives across save/load and will outlive any
		// individual GameClient if we ever support hot-swapping the client;
		// no unsubscribe needed today because GameClient and WorldState are
		// torn down together.
		SimState sim = worldState?.SimState;
		if (sim != null)
		{
			sim.onItemIdentified += OnSimItemIdentified;
			sim.onRecipeDiscovered += OnSimRecipeDiscovered;
			sim.onSpeciesDiscovered += OnSimSpeciesDiscovered;
			sim.onSpellLearned += OnSimSpellLearned;
		}
		onMobKilled += OnMobKilled;

		while (!_world.IsSpawnChunkReady(playerPosition))
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
		GD.Print($"[Load] Spawn-ready wait: {phaseSw.ElapsedMilliseconds}ms");
		phaseSw.Restart();

		// Sim builds the roster once from the authored templates (idempotent, so a
		// future disk-load carrying a party isn't rebuilt); we spawn a Player node
		// per member below.
		Party party = _world.EnsureParty(worldGenData?.startingParty);

		// Spawn every member as a Player node: the active member at the spawn
		// anchor (controlled), the rest evenly ringed around it and inactive
		// (they idle where placed). Suppress announcements during spawn-time
		// knowledge application so the starting potion / known recipes don't pop
		// banners on the first frame — Player.Initialize walks
		// WorldGenData.initialKnowledge under this gate.
		SuppressAnnouncements = true;
		try
		{
			SpawnParty(party, playerScene, worldGenData, playerPosition);
		}
		finally
		{
			SuppressAnnouncements = false;
		}

		// The scenario's initial knowledge was just applied to the active member's
		// provisional store during spawn — bank it into the permanent party pool
		// so it's shared from the first frame rather than sitting un-banked on
		// (and lost with) the starting character.
		sim?.BankActiveKnowledge();

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

		// Chart the spawn surroundings from frame one: the spawn chunks are loaded
		// now, so run one reveal pass and bank it into the party pool — otherwise a
		// fresh save opens to a blank world map (the per-tick reveal only fills the
		// active member's provisional store, invisible to the world map until banked).
		_world.Minimap?.RevealAtPlayerNow();
		sim?.BankActiveKnowledge();
		_world.Minimap?.RebuildExplorationDisplay();

		onPlayerSpawned?.Invoke(_player);

		// Hand the entity drain back to the steady in-game cadence and
		// enqueue the outer shell of chunks — those entities trickle in
		// over the next few seconds while the player is getting oriented.
		_world.MaxEntitiesPerFrame = Sim.DEFAULT_MAX_ENTITIES_PER_FRAME;
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

	// Instantiate one Player node per party member and place them around the
	// spawn anchor: the active member at the anchor (controlled), the rest
	// spread evenly on a ring and set inactive. Sets _player to the active one.
	void SpawnParty(Party party, PackedScene playerScene, WorldGenData worldGenData, Vector3 anchor)
	{
		_partyPlayers.Clear();
		int activeIndex = party.ActiveIndex;
		int inactiveCount = Math.Max(0, party.Count - 1);
		int ringSlot = 0;
		for (int i = 0; i < party.Count; i++)
		{
			bool active = i == activeIndex;
			Vector3 pos;
			if (active)
			{
				pos = anchor;
			}
			else
			{
				// Even ring so the controlled member (at the anchor) has room to
				// sit; gravity in Player.TickInactive settles each onto the ground.
				pos = RingPosition(anchor, ringSlot, inactiveCount);
				ringSlot++;
			}
			Player p = SpawnPartyMember(party[i], playerScene, worldGenData, pos, active);
			_partyPlayers.Add(p);
			if (active) { _player = p; }
		}
		// Every Player._Ready claims the audio listener, so the last member
		// spawned would otherwise own it — hand it to the controlled member.
		_player?.MakeAudioListenerCurrent();
	}

	// Even-spaced position on a ring of `ringCount` members around `anchor`.
	Vector3 RingPosition(Vector3 anchor, int slot, int ringCount)
	{
		float a = ringCount > 0 ? Mathf.Tau * slot / ringCount : 0f;
		return anchor + new Vector3(Mathf.Cos(a) * partyRingRadius, 0f, Mathf.Sin(a) * partyRingRadius);
	}

	// Teleport the living party to the campfire anchor: the controlled member at
	// the center (room to sit), the other survivors spread evenly around it. Dead
	// members are left where they fell (their body is the revivable corpse).
	// Used by the death flow to gather survivors, and by the camp Select-Character
	// confirm to re-center the newly-controlled member.
	public void GatherPartyAt(Vector3 anchor)
	{
		int ringCount = 0;
		foreach (Player p in _partyPlayers)
		{
			if (IsLiving(p) && p != _player) { ringCount++; }
		}
		int slot = 0;
		foreach (Player p in _partyPlayers)
		{
			if (!IsLiving(p)) { continue; }
			if (p == _player)
			{
				p.TeleportTo(anchor);
			}
			else
			{
				p.TeleportTo(RingPosition(anchor, slot, ringCount));
				slot++;
			}
		}
	}

	// A spawned, living party node (its member isn't fallen). A dead member's Player
	// node lingers where it fell as the revivable corpse, so gather/ring math skips it.
	// Aliveness reads the node's own PlayerState, so no roster-index alignment is assumed.
	static bool IsLiving(Player p) => p != null && p.Member is { IsDead: false };

	// The Player node hosting a given roster member, or null. Identity lookup — used
	// where control follows the roster's active member without assuming _partyPlayers
	// stays index-aligned with it.
	Player PlayerFor(PlayerState member)
	{
		if (member == null) { return null; }
		foreach (Player p in _partyPlayers)
		{
			if (p != null && p.Member == member) { return p; }
		}
		return null;
	}

	// Camp entry from the campfire interact (Campfire.Complete). The gameplay
	// effects — lighting the fire and banking the active member's field knowledge
	// / stashing carried materials (NotifyCampedAt) — run NOW, at the moment the
	// camp action completes; they are NOT gated on the fade, so an interrupted or
	// unwired fade can never leave the sim half-camped. The fade is purely
	// cosmetic: it hides the party gather / camera reframe and then opens the camp
	// screen. Input is gated for the whole transition; campScreen.Open keeps it
	// gated once open and campScreen.Close releases it.
	public void EnterCampWithFade(Campfire forge)
	{
		if (forge == null || campScreen == null || _player == null)
		{
			return;
		}
		InputSuppressed = true;
		// Lighting the fire makes it the world's LitCampfire (LitCampfireNode), which is
		// how Pray / the death select later reopen a full camp screen here.
		forge.Light();
		// The map reveal is armed but NOT shown here — it plays the next time the
		// player opens the map. Knowledge that newly landed in the pool is announced
		// (on top of the camp screen) inside NotifyCampedAt.
		NotifyCampedAt(forge.GlobalPosition);
		if (campFade != null && !campFade.Busy)
		{
			campFade.Play(() => campScreen.Open(_player, forge.GlobalPosition));
		}
		else
		{
			campScreen.Open(_player, forge.GlobalPosition);
		}
	}

	// The party is anchored to a new campfire — record it so a later death
	// gathers survivors here. Called when the player camps (CampScreen.Open).
	public void NotifyCampedAt(Vector3 campfirePosition)
	{
		_lastCampfirePosition = campfirePosition;
		// Snapshot the world map as the player last saw it BEFORE banking, so the
		// deferred reveal can animate from that state to the freshly-banked one.
		// Skip re-capturing when a reveal is already armed from an earlier camp the
		// player hasn't opened the map to see yet — that keeps the baseline pinned to
		// the genuinely last-seen state so the accumulated delta grows in one sweep.
		Minimap minimap = _world?.Minimap;
		if (minimap != null && !minimap.BankRevealArmed)
		{
			minimap.CaptureBankedRevealBaseline();
		}
		// Returning to a campfire commits the camp: Sim banks the active member's
		// provisional field knowledge into the permanent party pool (the "commit" in the
		// two-tier knowledge model) and drains their carried materials into the shared
		// stash. The returned flags say which knowledge categories gained, so we can
		// announce exactly what was recorded.
		EKnowledgeCategory banked = _world?.CommitCamp() ?? EKnowledgeCategory.None;
		AnnounceBankedKnowledge(banked);
		// Fold the freshly-banked reveal into the party pool display (the minimap,
		// which shows party ∪ active, updates now), then ARM the world-map reveal
		// WITHOUT playing it: PrepareBankedReveal rewinds the world map back to the
		// pre-camp baseline and holds it there. The sweep only fires the next time
		// the player opens the almanac to the map — in camp or later in the field
		// (see AlmanacScreen.ShowTab) — so the map isn't updated on entering camp.
		minimap?.RebuildExplorationDisplay();
		minimap?.PrepareBankedReveal();
	}

	// Announce a HUD line for each category of knowledge freshly banked into the
	// shared party pool at the campfire. Surfaced immediately: the event log lives
	// on the AnnouncementCanvas (above the camp screen's GUICanvas), so the lines
	// read on top of the open camp screen. Item identification has no campfire
	// notice (it announces in the field on ID).
	void AnnounceBankedKnowledge(EKnowledgeCategory banked)
	{
		if (banked.HasFlag(EKnowledgeCategory.Map))
		{
			Announce(new Announcement { type = EAnnouncementType.Notice, title = "Map Updated" });
		}
		if (banked.HasFlag(EKnowledgeCategory.Recipe))
		{
			Announce(new Announcement { type = EAnnouncementType.Notice, title = "Recipe Logged" });
		}
		if (banked.HasFlag(EKnowledgeCategory.Spell))
		{
			Announce(new Announcement { type = EAnnouncementType.Notice, title = "Spell Logged" });
		}
		if (banked.HasFlag(EKnowledgeCategory.Bestiary))
		{
			Announce(new Announcement { type = EAnnouncementType.Notice, title = "Bestiary Updated" });
		}
		if (banked.HasFlag(EKnowledgeCategory.Language))
		{
			Announce(new Announcement { type = EAnnouncementType.Notice, title = "Language Recorded" });
		}
	}

	// Recruit a talkable NPC into the party (fired by a RecruitToPartyAction in
	// the mob's conversation). Deep-clones the mob's authored party-member
	// template into a new roster member, spawns that member as an inactive Player
	// standing on the campfire ring, and despawns the mob. The newcomer idles at
	// camp — even though the player is out in the field talking — until the
	// player returns and can switch control to them (matching "show up at the
	// active campfire"). No-op (returns false) if the mob isn't recruitable or the
	// roster isn't ready. Idempotent by construction: the mob is removed here, so
	// its conversation can't fire this twice.
	public bool RecruitToParty(Mob mob)
	{
		if (mob?.RecruitTemplate == null || _playerScene == null)
		{
			return false;
		}
		// Sim clones the template into a new inactive roster member; we spawn the
		// matching Player node on the campfire ring. Appending to both in the same
		// order keeps _partyPlayers index-aligned with the roster.
		PlayerState member = _world?.RecruitMember(mob.RecruitTemplate);
		if (member == null)
		{
			return false;
		}

		// Place on the campfire ring alongside the other standing members. Sized to
		// the inactive count including the newcomer; the existing members keep their
		// slots (a slight unevenness) until the next GatherPartyAt re-rings them.
		int inactiveBefore = 0;
		for (int i = 0; i < _partyPlayers.Count; i++)
		{
			if (_partyPlayers[i] != null && _partyPlayers[i] != _player) { inactiveBefore++; }
		}
		Vector3 pos = RingPosition(_lastCampfirePosition, inactiveBefore, inactiveBefore + 1);
		Player p = SpawnPartyMember(member, _playerScene, _worldGenData, pos, active: false);
		_partyPlayers.Add(p);

		// Drop the player's highlight/current interactive if it still points at the
		// mob we're about to free. Recruit runs from the (now closed) conversation,
		// so the proximity re-detect that would otherwise clear a stale highlight
		// doesn't fire — without this, UpdateInteractHUD keeps the InteractHUD bound
		// to the despawned mob and Update() derefs its disposed GlobalPosition.
		if (_player != null && (ReferenceEquals(_player.CurInteractive, mob) || ReferenceEquals(_player.HighlightInteractive, mob)))
		{
			_player.ClearInteractive();
		}

		mob.Despawn();

		Announce(new Announcement
		{
			type = EAnnouncementType.PartyJoined,
			title = "Joined the Party",
			subtitle = string.IsNullOrEmpty(member.characterName) ? null : member.characterName,
		});
		return true;
	}

	Player SpawnPartyMember(PlayerState member, PackedScene playerScene, WorldGenData worldGenData, Vector3 position, bool active)
	{
		Player p = playerScene.Instantiate<Player>();
		// Only the active (controlled) member's events drive GameClient; inactive
		// members are wired the moment control switches to them (SwitchControlTo).
		if (active) { SubscribePlayerEvents(p); }
		sceneViewport.AddChild(p);
		p.Initialize(_world, worldGenData, member, position, Vector3.Zero);
		if (!active) { p.SetActive(false); }
		return p;
	}

	void SubscribePlayerEvents(Player p)
	{
		p.onHighlightChanged += OnPlayerHighlightChanged;
		p.onInteractChanged += OnPlayerInteractChanged;
		p.onLanguageLearned += OnPlayerLanguageLearned;
		p.onDied += OnPlayerDiedInternal;
		if (birdsEye != null) { p.onBirdsEye += birdsEye.SetActive; }
	}

	void UnsubscribePlayerEvents(Player p)
	{
		p.onHighlightChanged -= OnPlayerHighlightChanged;
		p.onInteractChanged -= OnPlayerInteractChanged;
		p.onLanguageLearned -= OnPlayerLanguageLearned;
		p.onDied -= OnPlayerDiedInternal;
		if (birdsEye != null) { p.onBirdsEye -= birdsEye.SetActive; }
	}

	// The party's Player nodes (index-aligned with the roster) and the index of
	// the controlled one. Read by the camp Select-Character screen.
	public IReadOnlyList<Player> PartyPlayers => _partyPlayers;
	// The roster's active member — the current selection, which the party tab
	// defaults its highlight to. Diverges from the controlled member only between
	// a Select-Character pick and the control transfer (camp close); falls back to
	// the controlled member's index if there's no party.
	public int ActivePartyIndex =>
		_world?.Party?.ActiveIndex ?? _partyPlayers.IndexOf(_player);

	// Mark a party member as active in the roster (data only). Control transfers
	// on the next SyncControlToActive — the camp Select-Character screen calls
	// this on Select and defers the transfer to camp exit. Returns true if the
	// active member actually changed.
	public bool SetPartyActive(int index) => _world?.SetPartyActive(index) ?? false;

	// Hand control — input, camera, World.player, HUD, audio listener — to
	// whichever member the roster currently marks active. The previously-
	// controlled member goes inactive (idles where it stands). No-op if that
	// member is already controlled. Called on camp exit after a Select-Character
	// choice, and by SwitchControlTo for the immediate debug switch.
	//
	// transferBelt: on a deliberate campfire character switch the attuned alchemy
	// spell travels with the player (moves from the outgoing member to the incoming
	// one). Left false for the death-respawn switch, where each survivor keeps their
	// own attunement.
	public void SyncControlToActive(bool transferBelt = false)
	{
		// Follow the roster's active member by identity, not index — no assumption that
		// _partyPlayers stays aligned with the roster.
		Player target = PlayerFor(_world?.Party?.Active);
		if (target == null || target == _player)
		{
			return;
		}
		Player outgoing = _player;
		if (outgoing != null)
		{
			UnsubscribePlayerEvents(outgoing);
			outgoing.SetActive(false);
			// Carry the attuned spell to the new character before the HUD rebinds.
			if (transferBelt)
			{
				outgoing.Inventory?.TransferAttunementTo(target.Inventory);
			}
		}
		SubscribePlayerEvents(target);
		target.SetActive(true);
		target.MakeAudioListenerCurrent();
		_player = target;
		_world.SetPlayer(target);
		hud?.RebindPlayer(target);
		camera?.SetInitialPosition(target.GlobalPosition);
		// Control moved to a different member — recompose the minimap fog-of-war
		// as party ∪ new-active so the previous member's un-banked field reveal
		// doesn't carry onto this character's map.
		_world.Minimap?.RebuildExplorationDisplay();
	}

	// Immediate switch to a specific member: mark active + transfer control now.
	// Used by the `party_next` debug command.
	public void SwitchControlTo(int index)
	{
		if (SetPartyActive(index))
		{
			SyncControlToActive();
		}
	}

	// Debug helper (party_next console command): cycle control to the next
	// member. No-op for a solo party.
	public void SwitchToNextPartyMember()
	{
		int active = _world?.Party?.ActiveIndex ?? -1;
		if (active < 0 || _partyPlayers.Count <= 1)
		{
			return;
		}
		SwitchControlTo((active + 1) % _partyPlayers.Count);
	}

	void OnSimItemIdentified(ItemData data)
	{
		if (data == null) { return; }
		SimState sim = _world?.WorldState?.SimState;
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
		Announce(new Announcement
		{
			type = EAnnouncementType.Recipe,
			title = "Recipe Discovered",
			subtitle = recipe.displayName.ToString(),
			icon = recipe.icon,
		});
	}

	void OnSimSpellLearned(SpellData spell)
	{
		if (spell == null) { return; }
		SimState sim = _world?.WorldState?.SimState;
		string name = sim != null ? sim.GetItemDisplayName(spell) : spell.displayName.ToString();
		Announce(new Announcement
		{
			type = EAnnouncementType.Recipe,
			title = "Spell Learned",
			subtitle = name,
			icon = spell.inventorySprite,
		});
	}

	void OnMobKilled(SpeciesData species, bool damagedByPlayer)
	{
		if (!damagedByPlayer) { return; }
		// A player-credited kill charts the species in the bestiary if it wasn't
		// already discovered by perception. DiscoverSpecies handles the
		// appearsInBestiary / already-known guards and fires the announcement.
		_world?.DiscoverSpecies(species);
	}

	void OnSimSpeciesDiscovered(SpeciesData species)
	{
		if (species == null) { return; }
		Announce(new Announcement
		{
			type = EAnnouncementType.MobDiscovered,
			title = "Creature Discovered",
			subtitle = SpeciesDisplayName(species),
		});
	}

	// Bestiary row label for a species: its own displayName when authored,
	// else the base mob's (the page title). Shared by the discovery + level-up
	// announcements so both read with the variant name.
	public static string SpeciesDisplayName(SpeciesData species)
	{
		if (species == null) { return string.Empty; }
		string name = species.displayName?.ToString();
		return string.IsNullOrEmpty(name) ? species.mob?.displayName.ToString() ?? string.Empty : name;
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
			Sim sim = Sim.Current;
			int nearbyPropCount = 0;
			FadeProbeResult probeResult = sim != null
				? sim.FadeProbe.Probe(cameraWorld, feet, head, tightProbeRadius, wideProbeRadius, foliagePlayerFadeProbeRange, out nearbyPropCount)
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

	// The ceiling cutaway's probe ring. Created lazily so a session that never
	// reaches the world never allocates it.
	private ClipIris _clipIris;
	private double _clipIrisDumpElapsed;

	// Builds the probe ring, resolves the base plane and the disk, and pushes both
	// to the shaders. The base goes through camera.SetClip, so it rides the
	// camera's own stability filter, fade curves and cap plane; the disk is the
	// only part that bypasses them, because its growth IS its transition.
	private void TickClipIris(double deltaSeconds)
	{
		// Pushed before the bail: the camera's manual reveal parks under the plateau
		// by this same clearance, and it runs in exactly the modes this returns from.
		if (camera != null)
		{
			camera.ClipClearance = clipClearance;
		}
		// ManualClipMode means someone else owns the height — the world editor
		// driving it from its cursor, or the bird's-eye lift holding it open.
		if (_player == null || camera == null || camera.ManualClipMode)
		{
			if (_clipIris != null)
			{
				RenderingServer.GlobalShaderParameterSet("clip_iris_enabled", false);
				RenderingServer.GlobalShaderParameterSet("clip_iris_radius", 0f);
				camera?.UpdateIrisCap(false, float.PositiveInfinity, Vector3.Zero);
			}
			return;
		}
		_clipIris ??= new ClipIris();
		_clipIris.RingSampleCount = clipIrisRingSamples;
		_clipIris.RingCount = clipIrisRingCount;
		_clipIris.RadiusMin = clipIrisRadiusMin;
		_clipIris.RadiusMax = Mathf.Max(clipIrisRadiusMax, clipIrisRadiusMin);
		_clipIris.OpeningRadius = clipIrisOpeningRadius;
		_clipIris.ProbeRangeSeconds = clipIrisRangeSeconds;
		_clipIris.BodyHeight = clipIrisBodyHeight;
		_clipIris.CeilingScanHeight = clipIrisCeilingScan;
		_clipIris.FloorTolerance = clipIrisFloorTolerance;
		_clipIris.OcclusionScanDistance = clipIrisOcclusionDistance;
		_clipIris.OcclusionLift = clipIrisOcclusionLift;
		_clipIris.OcclusionLiftHigh = clipIrisOcclusionLiftHigh;
		_clipIris.ShortCover = clipIrisShortCover;
		_clipIris.PlayerHiddenRise = clipIrisHiddenRise;
		_clipIris.Clearance = clipClearance;
		_clipIris.OpeningReach = clipIrisOpeningReach;
		// The same numbers the foliage cutaway uses — one authored shape, two effects.
		_clipIris.ShapeAspect = new Vector2(foliagePlayerFadeAspectHorizontal, foliagePlayerFadeAspectVertical);
		_clipIris.IrisPadding = clipIrisPadding;
		_clipIris.IrisGrowSeconds = clipIrisGrowSeconds;
		_clipIris.IrisShrinkSeconds = clipIrisShrinkSeconds;
		_clipIris.IrisHoldSeconds = clipIrisHoldSeconds;
		_clipIris.IrisHeightSeconds = clipIrisHeightSeconds;

		Vector3 playerPos = _player.GlobalPosition;
		// Swimmers and riders count as supported: they are held at a floor (the
		// waterline, the deck) as much as a grounded player is, and holding the plane
		// at the last shore they stood on for a whole crossing is not it. So do
		// climbers and mantlers — a traversal owns position and is neither grounded
		// nor falling, and holding the plane at the foot of the wall cut the climber
		// (and the wall) away once they passed it. Only genuine free flight — a jump,
		// a fall — holds it.
		bool supported = _player.IsGrounded || _player.IsInWater || _player.IsMounted
			|| _player.Climbing || _player.Mantling;
		_clipIris.Tick(Sim.Current, playerPos, supported, camera, (float)deltaSeconds);
		ClipIrisDebug.Draw(_clipIris, playerPos, (ClipIrisDebug.ELevel)CVars.clipIrisDebug.Value);

		camera.SetClip(_clipIris.BaseClipY, playerPos);

		bool iris = _clipIris.IrisActive;
		RenderingServer.GlobalShaderParameterSet("clip_iris_enabled", iris);
		RenderingServer.GlobalShaderParameterSet("clip_iris_radius", iris ? _clipIris.IrisRadius : 0f);
		if (iris)
		{
			RenderingServer.GlobalShaderParameterSet("clip_iris_center", _clipIris.IrisCenter);
			// The camera's screen basis, so the disk is a circle ON SCREEN rather
			// than a world circle the pitch would squash into an ellipse.
			RenderingServer.GlobalShaderParameterSet("clip_iris_right", _clipIris.ScreenRight);
			RenderingServer.GlobalShaderParameterSet("clip_iris_up", _clipIris.ScreenUp);
			RenderingServer.GlobalShaderParameterSet("clip_iris_edge", clipIrisEdgeSoftness);
			RenderingServer.GlobalShaderParameterSet("clip_iris_target", _clipIris.IrisClipY);
		}
		camera.UpdateIrisCap(iris, _clipIris.IrisClipY, _clipIris.IrisCenter);

		if (!CVars.clipIrisDump.Value)
		{
			return;
		}
		_clipIrisDumpElapsed += deltaSeconds;
		if (_clipIrisDumpElapsed >= 1.0)
		{
			_clipIrisDumpElapsed = 0.0;
			GD.Print($"[clip_iris] {_clipIris.Describe()}");
		}
	}

	// Is the ceiling cutaway currently hiding geometry at this world point? Above
	// the clip height, and — on the column path — in a column the mask actually
	// cuts. Callers that gate on "can the player see this" use it rather than
	// comparing against camera.Clip, which alone no longer answers the question.
	public bool IsCutAway(Vector3 worldPosition)
	{
		if (camera == null)
		{
			return false;
		}
		return worldPosition.Y >= ResolveClipHeight(worldPosition, camera.Clip);
	}

	// The clip height in force at a world point — the CPU twin of the shader's
	// height resolve, shared by prop culling and the "can the player see this"
	// gate so the two can't disagree about what is hidden. Inside the iris disk
	// that is the disk's lower plane; everywhere else it is the base.
	private float ResolveClipHeight(Vector3 worldPosition, float baseClip)
	{
		return _clipIris != null ? _clipIris.ClipHeightAt(worldPosition) : baseClip;
	}

	public override void _ExitTree()
	{
		// Silence any in-flight rumble when the game scene tears down (quit to
		// menu, scene swap) — the OS motor keeps running otherwise.
		_rumble.StopAll();
	}

	// One-shot scene-tree census for unattended runs, which have no console to
	// type `node_census` into. See CVars.nodeCensusDelay.
	private double _nodeCensusElapsed;
	private bool _nodeCensusDone;

	private void TickNodeCensusDelay(double deltaTime)
	{
		float delay = CVars.nodeCensusDelay.Value;
		if (_nodeCensusDone || delay <= 0f)
		{
			return;
		}
		_nodeCensusElapsed += deltaTime;
		if (_nodeCensusElapsed >= delay)
		{
			_nodeCensusDone = true;
			NodeCensus.Run();
			// Same unattended-diagnostic slot: a headless run can ask for one
			// subtree dump alongside the census by setting node_tree on the CLI.
			NodeCensus.DumpSubtree(CVars.nodeTree.Value);
			// The two reports answer halves of the same question — what is
			// resident, and what it costs — so an unattended run that asked for
			// one and enabled profiling gets both.
			if (CVars.profile.Value)
			{
				Profiler.DumpAndReset();
			}
		}
	}

	// Same unattended-diagnostic idea for world composition. Its own delay
	// rather than the census's, because the two answer different questions and
	// a run usually wants one or the other.
	private double _worldHistogramElapsed;
	private bool _worldHistogramDone;

	private void TickWorldHistogramDelay(double deltaTime)
	{
		float delay = CVars.worldHistogramDelay.Value;
		if (_worldHistogramDone || delay <= 0f)
		{
			return;
		}
		_worldHistogramElapsed += deltaTime;
		if (_worldHistogramElapsed >= delay)
		{
			_worldHistogramDone = true;
			if (Sim.Current?.WorldState != null)
			{
				GD.Print(Sim.Current.WorldState.DescribeBlockHistogram());
			}
		}
	}

	public override void _Process(double deltaTime)
	{
		using var _profProcess = Profiler.Sample("GameClient.Process");
		TickNodeCensusDelay(deltaTime);
		TickWorldHistogramDelay(deltaTime);

		// Push the foliage player-occlusion fade globals before the pause /
		// console gates — even while paused the camera or player anchors
		// can still drift (mid-pause shake, debug-cam fly), and a stale fade
		// volume would visibly punch the wrong hole in the canopy.
		using (Profiler.Sample("GameClient.FoliageGlobals"))
		{
			PushFoliageOcclusionGlobals(deltaTime);
		}

		// Ahead of the camera update below, which advances the clip fade this
		// commits to, and ahead of CullProps, which needs this tick's disk rather
		// than the last one's.
		using (Profiler.Sample("GameClient.ClipIris"))
		{
			TickClipIris(deltaTime);
		}

		// Drive rumble before the pause/console gate: an indefinite vibration
		// would otherwise stick on while paused. On a blocked frame StopAll
		// kills the motors immediately rather than letting impulses decay.
		if (_player == null || ConsoleUI.IsOpen || paused)
		{
			_rumble.StopAll();
		}
		else
		{
			_rumble.Tick((float)deltaTime);
		}

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
			// InputSuppressed in its Open(); Sim.Tick keeps running so a
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

		// Drive the fade-to-black for a fadeToBlack interactive action (Pray) off its
		// live interact progress, unwinding to clear the instant the action ends or is
		// cancelled. Runs regardless of InputSuppressed so the curtain still clears once
		// the completion effect opens the camp screen. campFade doubles as the overlay
		// (idle whenever a real camp fade isn't running, so SetManualDarkness owns it).
		campFade?.SetManualDarkness(_player.CurrentInteractiveFadesToBlack ? _player.ClientInteractProgress : 0f);

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
			using (Profiler.Sample("GameClient.ViewportSnap"))
			{
				viewportRig?.SnapAndUpscale();
			}
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
			else
			{
				followTime = camera.followTimeNormal;
			}
			using (Profiler.Sample("GameClient.UpdateCamera"))
			{
				camera.UpdateCamera(deltaTime, _player.GlobalPosition, followTime);
			}
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
		// Quantize every opted-in visual onto the grid the camera just snapped to.
		// Outside the branch chain above so it still runs under the bird's-eye
		// overview and the debug fly-cam, matching the per-node _Process this
		// replaced; each branch has already established its own grid by here.
		PixelSnap.TickAll(camera);

		// Sync the cap-mask camera AFTER the chunky-pixel snap so the mask
		// renders at the same snapped pose as the main scene. Mask
		// SubViewport size matches the inner pre-upscale size for 1:1
		// SCREEN_UV alignment.
		if (sceneViewport != null)
		{
			camera.SyncCapMaskCamera(sceneViewport.Size);
		}
		// Bird's-eye fly-up, the slow-mo death cam, and the camp zoom-in are all
		// zooms → radial channel.
		float radialBlur = Mathf.Max(camera?.CampRadialBlur ?? 0f, Mathf.Max(slowMotion?.RadialBlur ?? 0f, birdsEye?.MotionBlur ?? 0f));
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

		UpdateClimbHUD();

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
			_world?.DiscoverRegion(CurrentRegion);
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
		int shade = ws.GetCanopyShadeWorld(wx, wy, wz);
		GD.Print($"[SkyLight] voxel=({wx},{wy},{wz}) sun={sun}/{LightEngine.MAX_LIGHT} sky01={sun01:F2} canopy={canopy}/255 shade={shade}/255");
		// Walk the column upward from the player and dump (Y, sun, canopy,
		// shade) so we can see whether canopy density is present at the cluster
		// altitude, whether ComputeSunlight attenuated through it, and where the
		// leaves end and the derived shadow column begins.
		var col = new System.Text.StringBuilder();
		col.Append("[SkyLight column up]");
		for (int dy = 0; dy <= 14; dy++)
		{
			int yy = wy + dy;
			int s = ws.GetSunlightWorld(wx, yy, wz);
			int c = ws.GetCanopyAttenuationWorld(wx, yy, wz);
			int sh = ws.GetCanopyShadeWorld(wx, yy, wz);
			col.Append($" y{yy}:s={s},c={c},sh={sh}");
		}
		GD.Print(col.ToString());
	}

	static RegionData SampleRegion(Vector3 playerPos, WorldState ws)
	{
		ChunkState chunk = ws.GetChunk(Sim.WorldToChunkCoord(playerPos));
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

	// Bird's-eye lift (tree climb OR birds_eye consumable — they do the same
	// thing) has settled at its apex. Snapshot the wide reveal (fog + discovered
	// regions + markers) onto the world map, then open the map on it. Closing the
	// map (ESC / Map) descends the camera via OnBirdsEyeMapClosed. No-op if a modal
	// is already up.
	void OnBirdsEyeLiftApex()
	{
		if (_player == null || !_player.IsBirdsEye)
		{
			return;
		}
		if (almanacScreen == null || almanacScreen.Visible)
		{
			return;
		}
		Minimap minimap = _world?.Minimap;
		// Snapshot onto the world map, then grow the newly-surveyed ground in with
		// the SAME animated sweep the campfire bank uses. Baseline is the world map
		// as it stood at the last provisional update (or last camp); the snapshot
		// merges this climb's reveal into it; PrepareBankedReveal diffs the two and
		// rewinds the display to the baseline so the delta fades in on top.
		minimap?.CaptureBankedRevealBaseline();
		minimap?.SnapshotFieldRevealToWorldMap();
		_world?.SnapshotWorldMapReveal();
		minimap?.PrepareBankedReveal();
		// Opening the almanac to the world map fires the armed sweep (AlmanacScreen
		// .ShowTab → StartBankedReveal). The map opens instantly (no fade-to-black),
		// so the player watches the newly-surveyed ground grow in right away.
		almanacScreen.Open(AlmanacScreen.EAlmanacTab.WorldMap, this, onClose: OnBirdsEyeMapClosed);
	}

	// The bird's-eye world map closed — snap any in-progress reveal to fully charted
	// (in case it closed mid-sweep), then drop back down / end the overlook.
	void OnBirdsEyeMapClosed()
	{
		_world?.Minimap?.FinalizeBankedReveal();
		_player?.RequestEndBirdsEye();
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
			// Free-look orbit (camera preset 3): the mouse drives the camera, not
			// the aim cursor. Consume the motion here so it doesn't also deflect
			// the aim stick below.
			if (camera.FreeLookMode)
			{
				camera.AddMouseLook(mouseMotion.Relative * CVars.mouseSensitivity.Value);
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
			Vector2 motion = mouseMotion.Relative * CVars.mouseSensitivity.Value;

			// Positional aim: integrate raw motion as a world-unit cursor delta,
			// range-independent ("feels like a screen cursor"). Accumulated from
			// every motion event regardless of the directional disk's deadzone,
			// and consumed (cleared) once per reticle frame.
			_player.AddMouseAimDelta(motion * mousePositionalMetersPerPixel, camera.Yaw);

			// Directional aim: the same motion drives a virtual aim-stick clamped
			// to a (small, range-independent) disk; only its ANGLE matters for
			// facing. Past the deadzone so a near-center accumulator doesn't jitter
			// the heading.
			_mousePosition += motion;
			if (_mousePosition.LengthSquared() > mouseDirectionalDiskRadiusPx * mouseDirectionalDiskRadiusPx)
			{
				_mousePosition = _mousePosition.Normalized() * mouseDirectionalDiskRadiusPx;
			}
			if (_mousePosition.LengthSquared() >= mouseDirectionalDeadzonePx * mouseDirectionalDeadzonePx)
			{
				Vector2 deflection01 = _mousePosition / mouseDirectionalDiskRadiusPx;
				_player.ProcessMouseLook(deflection01, camera.Yaw);
			}
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		base._UnhandledInput(e);

		// Bird's-eye cancel runs before TogglePause because both actions are
		// bound to Escape — when the overlook is active the press should drop
		// the overview, not open the pause menu. Skipped while the scout world
		// map is up: there ESC must close the map first, whose onClose descends
		// the camera (OnScoutMapClosed) — otherwise the map would be stranded
		// open over the fly-down.
		if (_player != null && _player.IsBirdsEye && !(almanacScreen?.Visible ?? false) && e.IsActionPressed("ui_cancel"))
		{
			_player.RequestEndBirdsEye();
			GetViewport().SetInputAsHandled();
			return;
		}

		// Suppressed while a modal is up (almanac, merchant, etc.): Escape is
		// bound to both TogglePause and ui_cancel, so consuming it here would
		// open the pause menu instead of letting the modal close on its own
		// ui_cancel. TogglePause deliberately isn't gated on `paused` so Escape
		// still un-pauses (modals don't set `paused`).
		if (!InputSuppressed && e.IsActionPressed("TogglePause"))
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

	// Last clip this ran against, so a frame where it hasn't moved can skip every
	// entity that couldn't have moved either.
	private float _lastCullClip = float.NaN;

	void CullProps(float cameraClip)
	{
		using var _prof = Profiler.Sample("GameClient.CullProps");
		// The clip only changes when the player's ceiling context does. On every
		// other frame a STATIC entity's visibility cannot have changed, so only
		// things that can actually move get re-tested — the difference between
		// ~2300 GlobalPosition reads per frame and a few dozen. PhysicsBody3D is
		// the proxy for "can move": Mob and Loot are RigidBody3D, the player is a
		// CharacterBody3D, while props / foliage / roofs are plain Node3D or
		// StaticBody3D and never relocate once spawned.
		bool clipChanged = cameraClip != _lastCullClip;
		_lastCullClip = cameraClip;
		// The iris disk travels and grows with no clip-height change to notice, so
		// a static prop's visibility CAN change on a frame the scalar didn't move.
		// There is no window to scope that by — the disk is a handful of numbers,
		// not a grid — so while one is up every entity is re-tested. That is a
		// fraction of a second at a time, while the disk grows.
		bool diskActive = _clipIris != null && _clipIris.IrisActive;
		foreach ((Vector3I coord, List<Node3D> entities) in _world.ActiveEntities)
		{
			bool sweep = clipChanged || diskActive;
			foreach (Node3D entity in entities)
			{
				// A roof cuts itself away in-shader and carries passes that MUST
				// keep rendering once it does — a shadow proxy, and its cap-mask
				// copy. Hiding the node takes those children down with it, so the
				// shadow under the eaves vanishes the moment you step under the
				// roof. It is never hidden.
				if (entity is Roof)
				{
					continue;
				}
				if (!sweep && entity is not PhysicsBody3D)
				{
					continue;
				}
				Vector3 entityPos = entity.GlobalPosition;
				entity.Visible = entityPos.Y < ResolveClipHeight(entityPos, cameraClip);
			}
		}
	}

	void OnPlayerHighlightChanged(Node3D node)
	{
		UpdateHighlightOutline();
		UpdateInteractHUD();
	}

	// Currently outlined interactive, so UpdateHighlightOutline can skip the
	// reparent/shader churn when the meaningful target is unchanged.
	Node3D _outlinedNode;

	// Outline whichever interactive is currently meaningful — the one being used
	// (CurInteractive) if any, else the proximity highlight — so a solid or
	// sprite interactive stays ringed for the whole interaction, not just until
	// the press that starts it (which clears the highlight). Mirrors
	// UpdateInteractHUD's target selection.
	void UpdateHighlightOutline()
	{
		Node3D target = (_player?.IsBirdsEye ?? false)
			? null
			: (_player?.CurInteractive ?? _player?.HighlightInteractive) as Node3D;
		if (target == _outlinedNode)
		{
			return;
		}
		RemoveHighlight();
		if (target != null)
		{
			ApplyHighlight(target);
			_outlinedNode = target;
		}
		// Run the outline SubViewport only while something is actually outlined —
		// it's a full off-screen pass and stands idle on nearly every frame.
		camera?.SetOutlineMaskActive(_outlinedNode != null);
	}

	// Single source of truth for spawning/freeing the InteractHUD. Called
	// whenever the player's highlight OR current interactive changes: the
	// HUD survives the press-to-start transition (highlight clears the same
	// frame _curInteractive becomes non-null) by binding to whichever target
	// is currently meaningful.
	void UpdateInteractHUD()
	{
		// No interact prompt during the bird's-eye overview shot. Falls back to the
		// player's self-interactive when the player has pressed interact with nothing
		// highlighted (SelfMenuRequested) — that spawns the HUD purely so its options
		// modal can list the always-available self-actions (Pray, ...).
		IInteractive target = (_player?.IsBirdsEye ?? false)
			? null
			: _player?.CurInteractive ?? _player?.HighlightInteractive
				?? (_player != null && _player.SelfMenuRequested ? _player.SelfInteractive : null);
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

	// Spawn / free the climb prompt from the player's traversal preview. Driven
	// per-frame rather than from a change signal: the preview is a per-tick probe
	// of the terrain in front, not a state the player pushes.
	void UpdateClimbHUD()
	{
		// No prompt during the bird's-eye overview shot, matching the interact one.
		bool wanted = _player != null && !_player.IsBirdsEye
			&& _player.TraversalPreview != ETraversalPreview.None;
		if (_climbHUD != null && (!wanted || _climbHUD.Player != _player))
		{
			_climbHUD.QueueFree();
			_climbHUD = null;
			return;
		}
		if (_climbHUD == null && wanted && climbHudScene != null)
		{
			_climbHUD = ClimbHUD.Create(climbHudScene, camera, _player, worldHUD);
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
		// The previously-highlighted interactive may have despawned or streamed out
		// since we cached these — a freed Godot node isn't null (its wrapper survives
		// and throws on access), so gate teardown on IsInstanceValid, not a null check.
		if (IsInstanceValid(_meshHighlight))
		{
			_meshHighlight.SetSelected(false);
		}
		_meshHighlight = null;
		if (IsInstanceValid(_highlightOverlay))
		{
			_highlightOverlay.Visible = false;
			_highlightOverlay.Reparent(sceneViewport, false);
		}
		_outlinedNode = null;
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
			EHudTextType.Miss => hudTextMissScene,
			EHudTextType.Blocked => hudTextBlockedScene,
			EHudTextType.Parried => hudTextParriedScene,
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

	// Cancel an open boon-pick modal because the player was disturbed — took
	// damage or was otherwise interrupted mid-selection. No-op when the screen
	// isn't showing. Backs out through the same path as ui_cancel, so the
	// offering item (fairy corpse) stays unspent in the world / pack.
	public void InterruptUpgradeSelection()
	{
		upgradeScreen?.RequestCancel();
	}

	// Number of boon cards the fairy upgrade screen aims to show; the gold
	// filler pads up to this when too few candidate boons are viable.
	const int UpgradeChoiceCount = 3;

	// Narrow the fairy corpse's candidate boons to the ones worth offering right
	// now, then pad up to the corpse's choice count with the gold filler when too
	// few remain. The random roll already happened at spawn (Sim.ComposeFairyBoons
	// picks a fixed subset of the pool), so this only drops the boons that would be
	// a no-op right now — a restorative boon at full health, a lasting buff already
	// active (see IsBoonViable) — so the player never burns a corpse on a no-op
	// pick. The gold filler comes from SimData and is added at most once — it's
	// deliberately absent from the random pool, so it only ever appears here as
	// consolation, never as a random roll.
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
		int target = _world?.SimData?.fairyBoonChoiceCount ?? UpgradeChoiceCount;
		BoonData gold = _world?.SimData?.fairyBoonGold;
		if (viable.Count < target && gold != null && !viable.Contains(gold))
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

	// Forge upgrade offer. A Forge interaction calls this with the single offered
	// upgrade, whatever it would replace in that slot (null if the slot is empty),
	// the forge's level (the offered upgrade's tier), the replaced upgrade's tier,
	// the concrete slot both apply to (drives the offense/defense scaling shown),
	// and an accept callback that applies it. Mirrors the upgrade-screen gating
	// (input suppressed, HUD hidden, mouse freed) but always returns straight to
	// gameplay on close — the forge is used from the world, never nested inside
	// another modal.
	public void OpenForgeScreen(StatusEffectData offered, StatusEffectData replacing, int level, int replacingLevel, EUpgradeSlot slot, Action onAccept)
	{
		if (forgeScreen == null)
		{
			return;
		}
		InputSuppressed = true;
		if (hud != null) { hud.Visible = false; }
		Input.MouseMode = Input.MouseModeEnum.Visible;
		forgeScreen.Visible = true;

		forgeScreen.Init(
			() =>
			{
				onAccept?.Invoke();
				CloseForgeScreen();
			},
			CloseForgeScreen,
			offered,
			replacing,
			level,
			replacingLevel,
			slot);
	}

	void CloseForgeScreen()
	{
		if (forgeScreen != null)
		{
			forgeScreen.Visible = false;
		}
		InputSuppressed = false;
		if (hud != null) { hud.Visible = true; }
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	void OnPlayerInteractChanged(IInteractive interactive)
	{
		UpdateHighlightOutline();
		UpdateInteractHUD();
	}

	void OnMobSpawned(Mob mob)
	{
		_mobHuds?.Register(mob);
	}

	void OnMobRemoved(Mob mob)
	{
		_mobHuds?.Unregister(mob);
	}

	void OnDiscoverableSpawned(Discoverable discoverable)
	{
		if (discoverable.hudScene != null)
		{
			DiscoverableHud.Create(discoverable.hudScene, camera, discoverable, worldHUD);
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
		// A DoT killed the player mid-rest: SleepOverlay hands off to the death
		// screen and never calls EndSleep, so drop the wake callback here rather
		// than let it fire on the next rest. CampScreen's own onPlayerDied handler
		// tears down its camp state.
		_onSleepWake = null;
		onPlayerDied?.Invoke(player);

		// Hand the heartbeat over to its death wind-down (latched live rate,
		// eased to a stop synced to the DeathScreen fade). 0 → the controller
		// uses its own lowHealthDeathSlowdownSeconds fallback.
		screenEffects?.NotifyPlayerDied(deathScreen?.fadeOutSeconds ?? 0f);

		// Punch into slow-motion + zoom and hold it through the death-screen
		// fade. Cancel any pending finisher auto-release so it can't cut short.
		_victorySlowMoReleaseMs = 0;
		slowMotion?.Trigger();
		// A finisher focus could still be panning to a corpse when the player
		// dies — snap framing intent back to the player for the death cam.
		camera?.ClearFocus();

		// The fallen member's body stays where it died as a revivable corpse: Sim marks
		// the member dead; we make its node an inactive (dead-pose) standing body and
		// enable its revive interactive so a surviving member can bring it back.
		_world?.MarkMemberDead(player?.Member);
		player?.SetActive(false);
		player?.SetCorpseInteractable(true);
		// Relocated at the blackout below, not here — the death cam is still on
		// the body through the fade-out.
		_fallenBody = player;

		bool anySurvivors = (_world?.Party?.AliveCount ?? 0) > 0;
		if (deathScreen != null)
		{
			deathScreen.Show(this, anySurvivors
				? DeathScreen.EDeathOutcome.PartySelect
				: DeathScreen.EDeathOutcome.GameOver);
		}
		else if (anySurvivors)
		{
			// No screen wired (tests): resolve immediately so input isn't stranded.
			OnDeathBlackout();
			OpenDeathPartySelect();
		}
		else
		{
			QuitToMenu();
		}
	}

	// Called by DeathScreen once the screen is fully black (party-select outcome):
	// hand control to the first surviving member, gather the survivors at the last
	// campfire, and frame it. The dead member's body is left behind as a corpse.
	public void OnDeathBlackout()
	{
		int alive = _world?.Party?.FirstAliveIndex() ?? -1;
		if (alive < 0)
		{
			return;
		}
		// A body that died in water or in mid-air goes back to the last ground its
		// owner stood on — done under the black screen so the move is never seen,
		// and before the day roll, which can retire the member outright.
		_fallenBody?.ReturnBodyToLastGroundedPosition();
		_fallenBody = null;
		// Hand control to a living member FIRST — the death time-skip early-outs on a
		// dead controlled member, so a survivor must be driving before we roll the day.
		_world.SetPartyActive(alive);
		SyncControlToActive();
		// "Sleep off" the death: Sim advances to the next sunrise, grants the newly-
		// fallen member their one-day revive grace, and retires anyone whose deadline
		// the skip just passed.
		_world.ResolveDeathDayRoll();
		GatherPartyAt(_lastCampfirePosition);
		camera?.SetInitialPosition(_lastCampfirePosition);
		slowMotion?.Release();
		screenEffects?.ResetOnRespawn();
	}

	// Each sunrise Sim rolls the roster (meal reset + well-rested lottery, in
	// Sim.AdvanceToNextSunrise); the client re-applies the resulting per-member NODE
	// effects: the WellRested stat buff (the campfire glow follows the same flag,
	// gated on sitting at the fire in Player.UpdateWellRestedFx) and a top-off of
	// every carried lantern. A fountain is the only other refuel — a campfire
	// deliberately isn't. It also clears every member's attuned spell — a new day
	// resets the camp spell pick (the leader pick resets in Sim.RequireLeaderChoice),
	// so the next camp re-attunes. Subscribed to Sim.OnNewDay, the only day-advance
	// path, so this covers the camp sleep-to-sunrise, the death time-skip, and pray.
	void OnNewDayRefreshNodes(int dayNumber)
	{
		for (int i = 0; i < _partyPlayers.Count; i++)
		{
			_partyPlayers[i]?.RefreshWellRested();
			_partyPlayers[i]?.RefuelLantern();
			_partyPlayers[i]?.Inventory?.ClearAttunement();
		}
	}

	// Sim retired this fallen member (its revive deadline lapsed) and already dropped
	// it from the roster; we free the matching corpse Player node. Resolved by member
	// identity — the node still carries its Member reference after the roster removal —
	// so no roster-index alignment is assumed. Only dead members are ever reported, so
	// the controlled member is never destroyed.
	void OnPartyMemberExpired(PlayerState member)
	{
		Player corpse = PlayerFor(member);
		if (corpse == null)
		{
			return;
		}
		_partyPlayers.Remove(corpse);
		corpse.QueueFree();
	}

	// Called by DeathScreen after the fade-in reveals the campfire (party-select
	// outcome): open the camp Select-Character screen, locked to the party tab, so
	// the player must pick who to control. It manages its own input gating and
	// transfers control to the chosen survivor on close.
	public void OpenDeathPartySelect()
	{
		if (campScreen != null)
		{
			// CampScreen reads the lit fire live, so cooking enables itself once the respawn
			// fire streams in (or stays disabled if the player has no lit fire at all).
			campScreen.OpenPartySelect(_player, _lastCampfirePosition);
		}
		else
		{
			// No camp screen wired: just release input on the auto-picked survivor.
			InputSuppressed = false;
		}
	}

	// Resolution of a party member's Revive interactive (Player corpse → Complete).
	// The revive fx already played via the action's completion event; here we
	// restore the member and relocate them to the campfire as a selectable,
	// standing (not controlled) party member.
	public void RevivePartyMember(Player corpse)
	{
		if (corpse?.Member == null || !corpse.Member.IsDead)
		{
			return;
		}
		// Sim restores the member: it folds the fallen member's un-banked field
		// knowledge back into the reviving (active) member's provisional store and
		// clears the death flags. MergeFrom folds in the map reveal too, so recompose
		// the minimap display to surface it immediately when any knowledge moved.
		if (_world?.ReviveMember(_player?.Member, corpse.Member) == true)
		{
			_world.Minimap?.RebuildExplorationDisplay();
		}
		corpse.SetCorpseInteractable(false);
		corpse.Respawn(_lastCampfirePosition);
		corpse.SetActive(false);
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
		// Sim resets the controlled member's pools/effects, teleports them to spawn,
		// refills their lanterns (this path doesn't roll the day, so the sunrise refuel
		// won't fire), and recalls a surviving companion to the spawn point at full
		// health (one that died stays dead). We snap the camera and ease slow-mo back.
		_world?.RespawnControlledPlayer(_spawnPosition);
		camera.SetInitialPosition(_spawnPosition);

		// Ease back to real time + the resting zoom. The ease-out plays under the
		// DeathScreen fade-in (revealing from black).
		slowMotion?.Release();

		// Clear the death wind-down so the heartbeat goes fully idle (health is
		// restored, so the overlay ramp is 0); a fresh low-health episode will
		// re-engage it from scratch.
		screenEffects?.ResetOnRespawn();

		onPlayerRespawned?.Invoke(_player);
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
	public void BeginSleep(double hours, double healFractionPerHour)
	{
		if (_player == null || sleepOverlay == null || sleepOverlay.Busy || InputSuppressed)
		{
			return;
		}
		// Plain rest (tent): a fixed-hours nap (never rolls the day), EndSleep
		// releases the input gate on wake.
		_onSleepWake = null;
		_sleepToSunrise = false;
		InputSuppressed = true;
		sleepOverlay.Show(this, hours, healFractionPerHour);
	}

	// Wake callback for a modal-driven rest. When set, EndSleep hands the input
	// gate back to the caller (which stays open across the sleep) instead of
	// releasing it. Cleared on a clean wake and on death-in-sleep.
	Action _onSleepWake;

	// Whether the pending sleep advances to the next day's sunrise (clears the
	// player's effects + full-heals) vs. a short in-day nap (integrates effects +
	// fractional heal). Set by the Begin* entry points, read by PerformSleepAdvance.
	bool _sleepToSunrise;

	// Camp-screen rest entry point. The camp modal stays open (just hidden) across
	// the sleep, so we skip the modal guard BeginSleep uses for the tent path and
	// keep input gated the whole time. `toSunrise` selects the sleep-to-sunrise
	// path (else a fixed-hours nap). onWake fires when the fade-in completes so
	// the camp screen can re-show itself, still in camp state.
	public void BeginSleepFromCamp(double hours, double healFractionPerHour, Action onWake, bool toSunrise)
	{
		if (_player == null || sleepOverlay == null || sleepOverlay.Busy)
		{
			return;
		}
		_onSleepWake = onWake;
		_sleepToSunrise = toSunrise;
		InputSuppressed = true;
		// Camp music stops the moment the player sleeps; the wake plays the
		// time-of-day ambient cue (MusicManager.OnCampSleepWake via EndSleep).
		MusicManager.Instance?.OnCampSleepStart();
		sleepOverlay.Show(this, hours, healFractionPerHour);
	}

	// Called by SleepOverlay once the screen is fully black — the only moment
	// the skip is visible-safe (so an integrated DoT death and its slow-mo
	// death-cam happen behind the curtain).
	public void PerformSleepAdvance(double hours, double healFractionPerHour)
	{
		// Sim runs the whole skip behind the fade: to-sunrise rolls the day and
		// full-heals (a DoT can't kill the sleeper); a nap integrates effects over the
		// hours then heals a fraction; either way a surviving companion wakes at the
		// player's side and the world's spawns reset (gated on time actually passing).
		_world?.PerformSleepAdvance(hours, healFractionPerHour, _sleepToSunrise);
	}

	// Called by SleepOverlay when a clean wake's fade-in completes. A modal-driven
	// rest (camp) hands control back to its wake callback, which re-shows the
	// still-open modal and keeps the input gate; a plain rest (tent) releases it.
	public void EndSleep()
	{
		Action wake = _onSleepWake;
		_onSleepWake = null;
		if (wake != null)
		{
			wake.Invoke();
			// Camp wake: play the ambient cue for the phase the player woke in.
			MusicManager.Instance?.OnCampSleepWake();
		}
		else
		{
			InputSuppressed = false;
		}
	}

	// Resolution of the Pray self-action (PrayReturnHomeEffect, fired once the
	// screen is fully black). Sends the player home to their last campfire and wakes
	// them into the camp screen the next morning. Every state change here reuses the
	// existing camp/sleep path — the sleep-to-sunrise trio (advance the day, clear
	// transient effects, full-heal, exactly as PerformSleepAdvance's toSunrise branch)
	// plus the ordinary camp-screen open. The ONE deliberate omission is banking:
	// unlike EnterCampWithFade this never calls NotifyCampedAt, so the field knowledge
	// and materials the player gathered are NOT committed — that's the cost of the
	// free trip home.
	public void PrayReturnHome()
	{
		if (_player == null || _world == null)
		{
			return;
		}
		// Sim sends the player home: teleport to the campfire, sleep to sunrise (reset
		// transient effects + full-heal), refuel lanterns, and recall a surviving
		// companion — the same restore the camp sleep makes, but deliberately WITHOUT
		// banking (the cost of the free trip). We reframe the camera + open the camp.
		_world.ReturnHomeToSunrise(_lastCampfirePosition);
		camera?.SetInitialPosition(_lastCampfirePosition);
		// Open the camp screen without banking. Relight the home fire if it's resident;
		// CampScreen reads the lit node live (full cook/craft) once it streams in.
		LitCampfireNode?.Light();
		campScreen?.Open(_player, _lastCampfirePosition);
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
