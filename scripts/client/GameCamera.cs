using Godot;

public partial class GameCamera : Camera3D
{
	// `new` intentionally hides Camera3D.Current (an instance bool indicating
	// the active rendering camera) — we want the singleton-style "Current"
	// the rest of the codebase uses (Sim.Current, GameClient.Current, ...).
	public static new GameCamera Current { get; private set; }

	[Export] public float pitchDegrees = -65;
	[Export] public float distance = 80;
	// Y offset above the player root (which sits at the feet plane) used as
	// the camera's framing target. ~1m centers the body in frame instead of
	// pinning the screen center on the player's feet.
	[Export(PropertyHint.Range, "0,3,0.05")] public float followHeightOffset = 1f;
	[Export] public float rotationTime = 0.5f;
	// Shape of the yaw ease-out, applied as 1 - (1 - progress)^power.
	// 1 = linear, 2 = standard ease-out, 3+ = sharper landing (less time
	// decelerating). Tunable in the editor.
	[Export(PropertyHint.Range, "1,8,0.25")] public float rotationCurvePower = 4f;
	// Duration of the rotation motion-blur decay. Drives the post-process
	// motion_blur_strength uniform from 1 → 0 after a Q/E press.
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float rotationBlurDuration = 0.5f;
	[Export] public float followTimeNormal = 0.2f;
	[Export] public float followTimeSprinting = 0.4f;
	[Export] public float followTimeAirAscending = 0.5f;
	[Export] public float followTimeDashing = 1f;

	[ExportGroup("Free Look (preset 3)")]
	// Right-stick orbit speed at full deflection, radians/sec.
	[Export(PropertyHint.Range, "0.5,6,0.1")] public float freeLookStickSpeed = 2.5f;
	// Mouse orbit speed, radians per pixel of motion.
	[Export(PropertyHint.Range, "0.001,0.02,0.0005")] public float freeLookMouseSpeed = 0.005f;
	// Pitch clamp for the orbit (negative = looking down). Min is the steepest
	// top-down, Max the most level.
	[Export(PropertyHint.Range, "-89,-1,1")] public float freeLookPitchMinDegrees = -85f;
	[Export(PropertyHint.Range, "-89,-1,1")] public float freeLookPitchMaxDegrees = -10f;
	// Orbit radius as the camera lowers toward level. At the steepest (top-down)
	// pitch the radius is the full `distance`; at the most level pitch it shrinks
	// to freeLookMinDistance. The blend is squared against how far the pitch has
	// lowered, so the camera holds the far distance through the top of the range
	// and closes on the player more quickly as it approaches level.
	[Export(PropertyHint.Range, "1,40,0.5")] public float freeLookMinDistance = 30f;
	// Height above the player root (feet plane) that the orbit focuses on. The
	// normal follow uses followHeightOffset; free-look frames slightly higher.
	[Export(PropertyHint.Range, "0,3,0.05")] public float freeLookFocusHeight = 1.5f;

	[ExportGroup("Focus Subject")]
	// Generic "point the camera at a subject" override (a killed mob, a future
	// cinematic / dialogue target). FocusOn eases the framing anchor from the
	// player follow target toward the subject; ClearFocus eases it back. The
	// player still drives clip / shake / minimap — only the framing anchor moves.
	// Exponential time constant for that ease, in and out.
	[Export(PropertyHint.Range, "0.05,3,0.05")] public float focusBlendTime = 0.4f;
	// How far toward the subject to pull the framing. 1 = center the subject
	// outright; < 1 keeps the player partly in frame (a two-shot of both).
	[Export(PropertyHint.Range, "0,1,0.05")] public float focusWeight = 0.85f;

	[ExportGroup("Ceiling Cutaway")]
	// How far BELOW the cutaway height the cap plane sits. The cap is opaque and
	// depth-tested, so this is a tolerance band: geometry in (clip - bias, clip]
	// is below the clip (never dithered away) but above the cap plane, so it wins
	// the depth test and draws over the cap. That is deliberate for foliage and
	// props at the cutaway's lower edge — but anything embedded in a wall within
	// the band (window frames, lintels) punches through the cap the same way.
	// Keep it just large enough to avoid z-fighting with a surface sitting exactly
	// at the cutaway height.
	[Export(PropertyHint.Range, "0,1,0.01")] public float capPlaneYBias = 0.05f;

	// Tolerance the stability filter treats two clip heights as the same within.
	// Purely a jitter band — it used to double as the manual reveal's offset below
	// the plateau, so tuning that silently retuned the filter.
	private const float CLIP_MATCH_EPSILON = 0.5f;
	// How far below the surface overhead every clip plane parks. NOT an [Export]:
	// GameClient owns the one knob (clipClearance) and pushes it here, so the base
	// plane, the disk and this manual reveal cannot drift apart.
	public float ClipClearance = 0.5f;
	// Player eye offset above the foot position. Other systems (minimap
	// elevation reference, etc.) read this so the height the camera treats
	// as "looking from" stays consistent across features.
	public const float EYE_HEIGHT = 2f;
	// Vertical band size the cutaway snaps to. Public because the world editor's
	// plateau-snapped brushes build onto the same grid — one shared number, so
	// authored walls can't drift out of alignment with the bands that reveal them.
	public const float PLATEAU_STEP = 4f;
	// Asymmetric clip-fade durations. Going DOWN (cutaway opening / iris
	// growing) is slower and uses a stronger ease-out so the player gets
	// a moment to absorb the reveal of indoor space; going UP (iris
	// closing back to open canopy) is twice as fast with a lighter ease
	// — nothing meaningful to study on the reveal side, just a gentle
	// settle at the end. Both curves end with deceleration so the
	// leading edge "lands" gracefully rather than slamming to its final
	// position.
	[Export(PropertyHint.Range, "0.1,3,0.05")] public float clipFadeDownSeconds = 1.5f;
	[Export(PropertyHint.Range, "0.05,3,0.05")] public float clipFadeUpSeconds = 0.75f;
	// Number of consecutive frames a new target must match before SetClip
	// routes it to RequestClip. Drops single-frame transients — a doorway
	// threshold where the probe ring is split evenly between the room and
	// the street, a lintel the player passes under — that would otherwise
	// cause mid-iris band shifts and pop the band-edge pixels. Three frames
	// at 60Hz is ~50ms; too small to feel laggy for genuine ceiling changes,
	// large enough to absorb a blip.
	[Export(PropertyHint.Range, "1,12,1")] public int clipTargetStabilityFrames = 3;

	private float _pitchRadians => Mathf.DegToRad(pitchDegrees);
	private float _clip = float.PositiveInfinity;
	// Source Y for the in-progress fade. While `_clipBlend` < 1, shaders
	// blend between this and `_clip`. Equal to `_clip` when idle.
	private float _clipPrev = float.PositiveInfinity;
	// Most recent requested target + how many consecutive frames it's
	// matched. Drives the stability filter in SetClip — a new target has to
	// repeat for `clipTargetStabilityFrames` before it's committed to
	// RequestClip, so single-frame blips don't trigger band shifts mid-iris.
	private float _candidateTarget = float.PositiveInfinity;
	private int _candidateTargetFrames;
	// _clipBlend is the EASED value that gets pushed to the shader as
	// camera_clip_blend (iris position 0..1). _clipFadeT is the raw
	// linear 0..1 progress that AdvanceClipFade ticks each frame; the
	// ease curve maps t → blend. Two values because reversal needs to
	// invert visible state via blend, then back-solve t for the new
	// direction's curve so per-frame advance continues smoothly.
	private float _clipBlend = 1f;
	private float _clipFadeT = 1f;
	// Yaw is stored in RADIANS (consistent with Q/E rotations that use
	// DegToRad(90)). Initial value = 45° → DegToRad(45). A raw `45` here
	// would normalize to ~58.3° via 45 mod 2π, throwing off reflection-sun
	// alignment.
	private float _yaw = Mathf.Pi / 4f;
	private float _destYaw = Mathf.Pi / 4f;
	// Captured at the start of each Q/E rotation so the eased progress
	// interpolates from a stable anchor. Mid-rotation re-presses overwrite
	// these and restart the ease from the current intermediate yaw.
	private float _yawStart = Mathf.Pi / 4f;
	private float _rotationElapsed;
	private bool _rotating;
	// Free-look orbit (preset 3). _freeLook is set from the preset; _freeYaw /
	// _freePitch (radians) are the continuous orbit angles the mouse / right
	// stick drive, seeded from the live pose the first tick after enabling.
	private bool _freeLook;
	private float _freeYaw;
	private float _freePitch;
	private bool _freeLookInitialized;
	private Vector3 _followPosition;
	private bool _followInitialized;
	// Focus-subject override (see focusBlendTime / focusWeight). _focusNode is
	// tracked live while valid so a moving subject stays framed; _focusPoint is
	// the latched fallback so the framing holds (and the ease-back still has a
	// source) after a corpse despawns or for a one-shot point focus. _focusBlend
	// is the eased 0→1 mix toward the subject; _focusing is the held target state.
	private Node3D _focusNode;
	private Vector3 _focusPoint;
	private bool _hasFocusPoint;
	private float _focusBlend;
	private bool _focusing;
	private bool _clipAlways = false;
	private MeshInstance3D _clipCapPlane;
	// Fills the black interior inside the iris disk, where the cut sits lower than
	// the base plane the main cap is anchored to.
	private MeshInstance3D _irisCapPlane;
	private MeshInstance3D _waterCapPlane;
	private SubViewport _capMaskViewport;
	private Camera3D _capMaskCamera;
	// Selection-outline plumbing (mirrors the cap-mask: off-screen mask
	// SubViewport + camera synced to this one, plus a fullscreen composite quad
	// parented under the main camera that draws the ring from the mask). Only
	// active while an interactive is highlighted; the quad hides otherwise.
	private SubViewport _outlineMaskViewport;
	private Camera3D _outlineMaskCamera;
	private MeshInstance3D _outlineQuad;
	private CanvasLayer _capMaskDebugLayer;
	private TextureRect _capMaskDebugRect;
	// Visibility layers — main scene meshes default to bit 0 (layers = 1),
	// cap-mask geometry (added per-chunk in ChunkMesh) is on bit 1
	// (layers = 2). The main camera's cull_mask excludes bit 1 so it
	// doesn't see the mask geometry; the SubViewport camera's cull_mask
	// is bit 1 ONLY so it sees nothing else.
	public const uint MainSceneLayer = 1u << 0;
	// Perf bisection: switch either off-screen mask pass off entirely. Both run
	// UpdateMode.Always, so each is a full scene cull every frame over every
	// VisualInstance3D in the world — and the outline mask usually has nothing in
	// it at all. Disabling breaks the effect it feeds (the ceiling cutaway goes
	// stale, the selection outline vanishes); that is expected, these exist to
	// size the pass, not to ship off. Driven by the cap_mask_pass /
	// outline_mask_pass cvars.
	public void SetCapMaskPassEnabled(bool enabled)
	{
		if (_capMaskViewport != null)
		{
			_capMaskViewport.RenderTargetUpdateMode = enabled
				? SubViewport.UpdateMode.Always
				: SubViewport.UpdateMode.Disabled;
		}
	}

	public void SetOutlineMaskPassEnabled(bool enabled)
	{
		_outlineMaskPassAllowed = enabled;
		RefreshOutlineMaskUpdateMode();
	}

	// The outline pass only ever contains a highlighted interactive's meshes, so
	// on the overwhelming majority of frames it culls the whole world to draw
	// nothing — measured at ~1.9 ms/frame, the most expensive of the four
	// off-screen passes despite drawing the least (the cost is per-pass overhead:
	// render-target bind, clear, pipeline setup). GameClient tells us when an
	// interactive is outlined and we run the pass only then.
	public void SetOutlineMaskActive(bool active)
	{
		_outlineMaskActive = active;
		RefreshOutlineMaskUpdateMode();
	}

	private bool _outlineMaskPassAllowed = true;
	private bool _outlineMaskActive;

	private void RefreshOutlineMaskUpdateMode()
	{
		if (_outlineMaskViewport == null)
		{
			return;
		}
		if (!_outlineMaskPassAllowed)
		{
			_outlineMaskViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
			return;
		}
		// Once (not Disabled) when going idle: Godot renders exactly one more
		// frame — now empty — and then stops. Skipping that frame would freeze the
		// LAST outline in the render target, leaving a ghost outline on screen
		// after the player looks away.
		_outlineMaskViewport.RenderTargetUpdateMode = _outlineMaskActive
			? SubViewport.UpdateMode.Always
			: SubViewport.UpdateMode.Once;
	}

	public const uint CapMaskLayer = 1u << 1;
	// Selection-outline mask layer (bit 2, layer 3). A highlighted interactive's
	// meshes are temporarily ADDED to this layer (in addition to MainSceneLayer)
	// by InteractiveMeshHighlight; the outline mask camera culls to this layer
	// only, so the mask SubViewport sees just the selected model's silhouette.
	// The meshes stay on MainSceneLayer too so the main camera still draws them
	// once; the extra bit just also makes them visible to the mask camera.
	// MUST be a bit no other off-screen projector culls to — bit 3 is
	// BlockLightShadowProjector.SHADOW_PROXY_LAYER_MASK and bit 4 is
	// GroundStainProjector.STAIN_PROXY_LAYER_MASK. Reusing bit 4 caused the
	// GroundStainProjector to render highlighted props into the ground-stain RT,
	// smearing the model's color onto nearby terrain. Bit 2 is unshared.
	public const uint OutlineMaskLayer = 1u << 2;

	[ExportGroup("Camp Framing")]
	// Resting at a campfire (CampScreen → SetCampMode) lowers the pitch toward
	// campPitchDegrees, pulls the orbit radius in to campDistance, zooms the ortho
	// view in by campZoomPixelSteps pixel-scale steps (same "+1 pixel size" zoom as
	// SlowMotionController), re-frames on the campfire instead of the player, and
	// layers a very slow idle wobble on top. A radial motion blur tracks the
	// transition velocity, peaking as the camera leaves each resting point and
	// tapering to 0 at the held framing.
	[Export] public float campPitchDegrees = -30f;
	// Orbit radius while camped — eases from the live `distance` to this on engage.
	[Export] public float campDistance = 30f;
	// Perspective FOV while camped (only applied in perspective presets; ortho
	// presets zoom via campZoomPixelSteps). The resting FOV is captured on engage
	// and restored on leave.
	[Export(PropertyHint.Range, "10,90,1")] public float campFov = 35f;
	[Export(PropertyHint.Range, "0,4,1")] public int campZoomPixelSteps = 1;
	[Export(PropertyHint.Range, "0.05,3,0.05")] public float campTransitionSeconds = 0.5f;
	[Export(PropertyHint.Range, "0,1,0.05")] public float campMotionBlurPeak = 0.8f;
	// Slow idle wobble layered on the held camp framing so the shot breathes.
	// Amplitude in degrees; period in seconds (large = very slow). Yaw and pitch
	// oscillate at slightly detuned frequencies for an organic drift.
	[Export(PropertyHint.Range, "0,5,0.1")] public float campWobbleAmplitudeDegrees = 1.2f;
	[Export(PropertyHint.Range, "1,40,0.5")] public float campWobblePeriodSeconds = 14f;

	// Camp framing state. _campActive is true from SetCampMode(true) until the
	// ease-out fully completes; _campBlend is the raw 0..1 progress toward
	// _campTarget; _campBaseSize is the resting ortho Size captured at engage so
	// the zoom and its restore ride the same anchor.
	private bool _campActive;
	private float _campTarget;
	private float _campBlend;
	private float _campBaseSize = 1f;
	// Resting FOV captured on engage so the perspective zoom and its restore ride
	// the same anchor even if Fov is retuned while camped.
	private float _campBaseFov;
	// World-space framing target while camped: the campfire (lifted by
	// followHeightOffset), so the held shot centers the fire, not the player.
	private Vector3 _campFocusPoint;
	private bool _hasCampFocus;
	// Wall-clock accumulator driving the slow idle wobble (presentational only —
	// stays on render delta so slow-mo doesn't drag it).
	private float _campWobbleTime;
	private float _campRadialBlur;
	// Radial zoom-blur from the camp transition, folded into the post-process
	// alongside SlowMotion / BirdsEye by GameClient. 0 whenever idle.
	public float CampRadialBlur => _campRadialBlur;

	private readonly CameraShake _shake = new();
	public CameraShake Shake => _shake;

	// Motion-blur state for camera rotation. Strength is set to 1.0 on
	// RotateLeft/Right and decays in UpdateCamera over rotationBlurDuration.
	// GameClient.UpdatePostProcess pushes both to post_process.gdshader.
	// Direction is screen-space: positive X when objects sweep right (camera
	// turning left), negative X for the opposite.
	private float _rotationBlur;
	private Vector2 _rotationBlurDir = Vector2.Right;
	public float RotationBlurStrength => _rotationBlur;
	public Vector2 RotationBlurDir => _rotationBlurDir;

	public float Clip => _clip;
	// True once the clip fade has run out and both planes agree. Consumers that
	// want to act on the SETTLED cutaway rather than on a value still animating
	// gate on this — acting mid-fade is what makes a second cut flip on and off.
	public bool ClipSettled => _clipFadeT >= 1f;
	public float Yaw => _yaw;
	// True while a Q/E yaw tween is still easing toward its target.
	public bool IsRotating => _rotating;
	// True whenever the camera is clipping the world above the player —
	// either an auto-detected ceiling raycast hit between player and camera
	// or `_clipAlways` forcing the next-plateau cutaway. Read by the minimap
	// to swap to its indoor (slice) view in lockstep with the camera.
	public bool IsIndoorMode => !float.IsPositiveInfinity(_clip);
	public MeshInstance3D WaterCapPlane => _waterCapPlane;
	public bool ManualClipMode { get; set; } = false;

	// Writes an angle preset's framing into the live camera fields. Called only
	// on a camera_preset CVar change (and once at Init) — never per frame — so
	// editing pitchDegrees / distance / Fov in the inspector while the game runs
	// sticks until the next preset swap, leaving live tuning intact.
	public void ApplyAngleSettings(CameraAngleSettings settings)
	{
		pitchDegrees = settings.PitchDegrees;
		distance = settings.Distance;
		// Re-seed the orbit angles from the current pose on the next tick when
		// switching into free-look, so it picks up wherever the fixed camera was.
		_freeLook = settings.FreeLook;
		_freeLookInitialized = false;
		if (settings.Perspective)
		{
			Projection = ProjectionType.Perspective;
			Fov = settings.Fov;
		}
		else
		{
			Projection = ProjectionType.Orthogonal;
		}
	}

	public void Init(Node parent)
	{
		Current = this;
		ApplyAngleSettings(CameraAngleSettings.FromPreset(CVars.cameraPreset.Value));

		// Main camera only sees the main scene layer; the cap-mask geometry
		// (added per-chunk on CapMaskLayer) is invisible here.
		CullMask = MainSceneLayer;

		// Off-screen render target that builds a per-pixel mask of "where
		// the cap should draw." SubViewport shares the parent's World3D
		// (own_world_3d=false) so it sees the same chunk meshes without
		// us needing to mirror the scene tree, but its camera's cull_mask
		// is CapMaskLayer ONLY — it sees just the mask MeshInstance3Ds
		// added per-chunk in ChunkMesh, never the visible terrain or
		// sprites. Size is matched to the inner pre-upscale viewport in
		// SyncCapMaskCamera so SCREEN_UV in clip_cap maps 1:1.
		_capMaskViewport = new SubViewport();
		_capMaskViewport.OwnWorld3D = false;
		_capMaskViewport.HandleInputLocally = false;
		_capMaskViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
		_capMaskViewport.RenderTargetClearMode = SubViewport.ClearMode.Always;
		_capMaskViewport.TransparentBg = false;
		_capMaskViewport.Disable3D = false;
		_capMaskViewport.Msaa3D = Viewport.Msaa.Disabled;
		_capMaskViewport.Size = new Vector2I(2, 2);
		parent.AddChild(_capMaskViewport);

		_capMaskCamera = new Camera3D();
		_capMaskCamera.CullMask = CapMaskLayer;
		_capMaskCamera.Current = true;
		// Clear to WHITE = "cap should draw here." The terrain mask material
		// renders BLACK over visible (below-clip) terrain so those pixels
		// fail the cap's `mask >= 0.5` test and the cap doesn't draw
		// there. Above-clip front-faces are discarded so the white clear
		// shows through. The back-face mask material then writes white
		// over any underground front-faces that painted black through
		// other clipped solids, restoring the cap mask in those zones.
		// Environment is stripped of every non-essential effect since the
		// mask render only needs raw albedo writes — no lighting, no
		// post-process, no auto-exposure.
		var maskEnv = new Environment();
		maskEnv.BackgroundMode = Environment.BGMode.Color;
		maskEnv.BackgroundColor = new Color(1, 1, 1, 1);
		maskEnv.AmbientLightSource = Environment.AmbientSource.Disabled;
		maskEnv.ReflectedLightSource = Environment.ReflectionSource.Disabled;
		maskEnv.TonemapMode = Environment.ToneMapper.Linear;
		_capMaskCamera.Environment = maskEnv;
		_capMaskViewport.AddChild(_capMaskCamera);

		// Debug overlay: drives the `cap_mask_debug` CVar. When toggled on,
		// draws the SubViewport's texture as a full-screen TextureRect so
		// the mask is directly visible on top of the game.
		_capMaskDebugLayer = new CanvasLayer();
		_capMaskDebugLayer.Layer = 100;
		parent.AddChild(_capMaskDebugLayer);
		_capMaskDebugRect = new TextureRect();
		_capMaskDebugRect.Texture = _capMaskViewport.GetTexture();
		_capMaskDebugRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_capMaskDebugRect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
		_capMaskDebugRect.StretchMode = TextureRect.StretchModeEnum.Scale;
		_capMaskDebugRect.Visible = false;
		_capMaskDebugLayer.AddChild(_capMaskDebugRect);

		// --- Selection outline mask -------------------------------------
		// Off-screen viewport that renders ONLY the currently-selected
		// interactive's meshes (they get temporarily added to OutlineMaskLayer)
		// as an alpha-coverage silhouette. TransparentBg + an alpha-writing
		// camera env means coverage is independent of the model's albedo — a
		// dark-textured fold still reads as "inside," so the outline traces the
		// true screen silhouette. Shares World3D so no scene mirroring needed.
		_outlineMaskViewport = new SubViewport();
		_outlineMaskViewport.OwnWorld3D = false;
		_outlineMaskViewport.HandleInputLocally = false;
		// Starts idle — GameClient flips it Always via SetOutlineMaskActive the
		// moment an interactive is outlined. See RefreshOutlineMaskUpdateMode.
		_outlineMaskViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
		_outlineMaskViewport.RenderTargetClearMode = SubViewport.ClearMode.Always;
		_outlineMaskViewport.TransparentBg = true;
		_outlineMaskViewport.Disable3D = false;
		_outlineMaskViewport.Msaa3D = Viewport.Msaa.Disabled;
		_outlineMaskViewport.Size = new Vector2I(2, 2);
		parent.AddChild(_outlineMaskViewport);

		_outlineMaskCamera = new Camera3D();
		_outlineMaskCamera.CullMask = OutlineMaskLayer;
		_outlineMaskCamera.Current = true;
		var outlineEnv = new Environment();
		outlineEnv.BackgroundMode = Environment.BGMode.Color;
		outlineEnv.BackgroundColor = new Color(0, 0, 0, 0);
		outlineEnv.AmbientLightSource = Environment.AmbientSource.Disabled;
		outlineEnv.ReflectedLightSource = Environment.ReflectionSource.Disabled;
		outlineEnv.TonemapMode = Environment.ToneMapper.Linear;
		_outlineMaskCamera.Environment = outlineEnv;
		_outlineMaskViewport.AddChild(_outlineMaskCamera);

		// Fullscreen composite quad: samples the mask and paints the ring.
		// Parented under this (the main camera) like FogQuad so it always frames
		// the screen; on MainSceneLayer so only the main camera draws it. High
		// render priority so the ring sits over the world (and the cap planes).
		var outlineShader = GD.Load<Shader>("res://shaders/mesh_outline.gdshader");
		var outlineMaterial = new ShaderMaterial();
		outlineMaterial.Shader = outlineShader;
		outlineMaterial.RenderPriority = 8;
		outlineMaterial.SetShaderParameter("outline_mask_tex", _outlineMaskViewport.GetTexture());
		var outlineQuadMesh = new QuadMesh();
		outlineQuadMesh.Size = new Vector2(2, 2);
		_outlineQuad = new MeshInstance3D();
		_outlineQuad.Mesh = outlineQuadMesh;
		_outlineQuad.MaterialOverride = outlineMaterial;
		_outlineQuad.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		_outlineQuad.ExtraCullMargin = 16384f;
		_outlineQuad.Layers = MainSceneLayer;
		_outlineQuad.Position = new Vector3(0, 0, -2);
		_outlineQuad.Visible = false;
		AddChild(_outlineQuad);

		var capShader = GD.Load<Shader>("res://shaders/clip_cap.gdshader");
		var capMaterial = new ShaderMaterial();
		capMaterial.Shader = capShader;
		capMaterial.RenderPriority = 1;
		capMaterial.SetShaderParameter("cap_mask_tex", _capMaskViewport.GetTexture());

		var planeMesh = new PlaneMesh();
		planeMesh.Size = new Vector2(1000, 1000);

		_clipCapPlane = new MeshInstance3D();
		_clipCapPlane.Mesh = planeMesh;
		_clipCapPlane.MaterialOverride = capMaterial;
		_clipCapPlane.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		_clipCapPlane.Visible = false;
		parent.AddChild(_clipCapPlane);

		// Second cap, for the iris disk's lower plane. Same shader and same mask —
		// only the height and which region it fills differ, and the shader's
		// iris_inside_disk flag is what splits the screen between the two so
		// neither draws over the other's fill.
		var irisCapMaterial = (ShaderMaterial)capMaterial.Duplicate();
		irisCapMaterial.SetShaderParameter("iris_inside_disk", true);
		_irisCapPlane = new MeshInstance3D();
		_irisCapPlane.Mesh = planeMesh;
		_irisCapPlane.MaterialOverride = irisCapMaterial;
		_irisCapPlane.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		_irisCapPlane.Visible = false;
		parent.AddChild(_irisCapPlane);

		var waterCapShader = GD.Load<Shader>("res://shaders/water_clip_cap.gdshader");
		var waterCapMaterial = new ShaderMaterial();
		waterCapMaterial.Shader = waterCapShader;
		waterCapMaterial.RenderPriority = 2;
		// Same two ripple normal-map textures that voxel_water uses, so the
		// cap surface animates continuously with the water beneath it.
		var rippleA = GD.Load<Texture2D>("res://assets/textures/water_ripple_a.tres");
		var rippleB = GD.Load<Texture2D>("res://assets/textures/water_ripple_b.tres");
		waterCapMaterial.SetShaderParameter("ripple_tex_a", rippleA);
		waterCapMaterial.SetShaderParameter("ripple_tex_b", rippleB);

		_waterCapPlane = new MeshInstance3D();
		_waterCapPlane.Mesh = planeMesh;
		_waterCapPlane.MaterialOverride = waterCapMaterial;
		_waterCapPlane.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		_waterCapPlane.Visible = false;
		parent.AddChild(_waterCapPlane);

		GlobalRotation = new Vector3(_pitchRadians, _yaw, 0);
		_destYaw = GlobalRotation.Y;
		_yaw = _destYaw;

		PushClipGlobals();
	}

	public void SetCapMaskDebugVisible(bool visible)
	{
		if (_capMaskDebugRect != null)
		{
			_capMaskDebugRect.Visible = visible;
		}
	}

	// The world-space framing anchor for a given player position: the point
	// the camera centers on, lifted by followHeightOffset so the body (not the
	// feet) sits in frame. Single source of truth shared by the normal follow
	// (SetInitialPosition / UpdateCamera) and the bird's-eye driver so the two
	// paths stay aligned and hand off without a pop.
	public Vector3 GetFollowTarget(Vector3 playerPosition)
	{
		return playerPosition + new Vector3(0f, followHeightOffset, 0f);
	}

	public void SetInitialPosition(Vector3 playerPosition)
	{
		Vector3 target = GetFollowTarget(playerPosition);
		_followPosition = target;
		_followInitialized = true;
		GlobalPosition = target + GlobalTransform.Basis.Z * distance;
	}

	// Advances the in-progress Q/E yaw tween and decays rotation motion blur.
	// Split out of UpdateCamera so the bird's-eye driver (which manages
	// position/clip itself) can still keep yaw rotation responsive without
	// also pulling in the follow/clip work it doesn't want.
	public void TickRotation(float deltaTime)
	{
		if (_rotating)
		{
			_rotationElapsed += deltaTime;
			float progress = Mathf.Min(1f, _rotationElapsed / Mathf.Max(0.0001f, rotationTime));
			float eased = 1f - Mathf.Pow(1f - progress, rotationCurvePower);
			_yaw = Mathf.LerpAngle(_yawStart, _destYaw, eased);
			if (progress >= 1f)
			{
				_yaw = _destYaw;
				_rotating = false;
			}
		}

		if (_rotationBlur > 0f && rotationBlurDuration > 0f)
		{
			_rotationBlur = Mathf.Max(0f, _rotationBlur - deltaTime / rotationBlurDuration);
		}
	}

	// Enter / leave camp framing, focusing on the campfire at `campfirePosition`
	// (default when leaving). Capturing the resting Size / FOV on engage lets the
	// zoom-in and its later restore share one anchor even if they're retuned while
	// camped. Mirrors SlowMotionController's Trigger/Release shape.
	public void SetCampMode(bool active, Vector3 campfirePosition = default)
	{
		_campTarget = active ? 1f : 0f;
		if (active)
		{
			// Lift the focus to the fire (not its base) with the same offset the
			// player follow uses, so the framing reads centered on the flames.
			_campFocusPoint = GetFollowTarget(campfirePosition);
			_hasCampFocus = true;
		}
		if (active && !_campActive)
		{
			_campBaseSize = Size;
			_campBaseFov = Fov;
			_campWobbleTime = 0f;
			_campActive = true;
		}
	}

	// Advances the camp pitch/zoom ease and the matching radial blur. Returns the
	// eased blend (0 = resting framing, 1 = full camp framing) for the caller's
	// pitch lerp. No-op returning 0 whenever camp is idle, so normal follow is
	// byte-identical.
	private float TickCamp(float deltaTime)
	{
		if (!_campActive)
		{
			return 0f;
		}
		float rate = campTransitionSeconds > 0f ? deltaTime / campTransitionSeconds : 1f;
		_campBlend = Mathf.MoveToward(_campBlend, _campTarget, rate);
		float eased = 1f - (1f - _campBlend) * (1f - _campBlend);

		int basePixelScale = Mathf.Max(1, CVars.pixelScale.Value);
		float zoomFactor = (basePixelScale + campZoomPixelSteps) / (float)basePixelScale;
		Size = Mathf.Lerp(_campBaseSize, _campBaseSize / zoomFactor, eased);

		// Heaviest as the camera leaves a resting point (blend near 0/1 extremes,
		// where the ease-out moves fastest), 0 at the held framing.
		_campRadialBlur = (1f - eased) * campMotionBlurPeak;

		if (_campTarget <= 0f && _campBlend <= 0f)
		{
			Size = _campBaseSize;
			_campRadialBlur = 0f;
			_campActive = false;
		}
		return eased;
	}

	// True while preset 3 (free-look orbit) is active. GameClient reads this to
	// route mouse motion into AddMouseLook instead of the aim cursor.
	public bool FreeLookMode => _freeLook;

	// Polls the right-stick Look axes and advances the free-look orbit angles.
	// Seeds from the live pose the first tick after enabling so the swap is
	// seamless. Right stick only; mouse motion arrives via AddMouseLook.
	private void TickFreeLook(float deltaTime)
	{
		if (!_freeLookInitialized)
		{
			_freeYaw = _yaw;
			_freePitch = _pitchRadians;
			_freeLookInitialized = true;
		}

		Vector2 look = new(
			Input.GetActionStrength("LookRight") - Input.GetActionStrength("LookLeft"),
			Input.GetActionStrength("LookDown") - Input.GetActionStrength("LookUp"));
		_freeYaw -= look.X * freeLookStickSpeed * deltaTime;
		_freePitch += look.Y * freeLookStickSpeed * deltaTime;
		ClampFreePitch();
	}

	// Feeds a raw mouse-motion delta (pixels) into the free-look orbit. X yaws,
	// Y pitches (mouse down looks down). No-op unless free-look is active.
	public void AddMouseLook(Vector2 relative)
	{
		if (!_freeLook)
		{
			return;
		}
		_freeYaw -= relative.X * freeLookMouseSpeed;
		_freePitch += relative.Y * freeLookMouseSpeed;
		ClampFreePitch();
	}

	private void ClampFreePitch()
	{
		_freePitch = Mathf.Clamp(
			_freePitch,
			Mathf.DegToRad(freeLookPitchMinDegrees),
			Mathf.DegToRad(freeLookPitchMaxDegrees));
	}

	// tickRotation: false for a caller that already advanced the yaw tween itself
	// this frame (the world editor, which has to know the final yaw before it can
	// place the framing anchor it passes in here).
	public void UpdateCamera(double deltaTime, Vector3 playerPosition, float followTime, bool tickRotation = true)
	{
		if (tickRotation)
		{
			TickRotation((float)deltaTime);
		}

		Vector3 target = GetFollowTarget(playerPosition);
		if (!_followInitialized)
		{
			_followPosition = target;
			_followInitialized = true;
		}
		else
		{
			float followT = 1f - Mathf.Pow(0.01f, (float)deltaTime / Mathf.Max(0.0001f, followTime));
			_followPosition = _followPosition.Lerp(target, followT);
		}

		Vector3 framingAnchor = ResolveFocusAnchor(_followPosition, (float)deltaTime);

		float orbitDistance = distance;
		if (_freeLook)
		{
			// Mouse/right-stick orbit. Set the orientation to the free yaw/pitch,
			// then push the camera back along its own +Z (the "ray back" from the
			// target) by orbitDistance so it looks straight at the framing anchor.
			// Mirror _yaw so camera-relative movement and everything reading Yaw
			// follows.
			TickFreeLook((float)deltaTime);
			_yaw = _freeYaw;
			GlobalRotation = new Vector3(_freePitch, _freeYaw, 0);

			// Frame freeLookFocusHeight above the player instead of the normal
			// followHeightOffset. The delta is constant, so the horizontal follow
			// smoothing already baked into framingAnchor is preserved.
			framingAnchor += Vector3.Up * (freeLookFocusHeight - followHeightOffset);

			// Pull the orbit radius in as the camera lowers toward level. lower = 0
			// at the steepest pitch, 1 at the most level; squaring it holds the far
			// distance through the top of the range and closes on the player faster
			// near level.
			float pitchMin = Mathf.DegToRad(freeLookPitchMinDegrees);
			float pitchMax = Mathf.DegToRad(freeLookPitchMaxDegrees);
			float lower = Mathf.InverseLerp(pitchMin, pitchMax, _freePitch);
			orbitDistance = Mathf.Lerp(distance, freeLookMinDistance, lower * lower);
		}
		else
		{
			// Camp framing eases the pitch lower, the orbit in to campDistance, the
			// perspective FOV to campFov, the framing onto the campfire, and layers a
			// slow wobble. TickCamp also drives the ortho Size and the camp motion
			// blur, returning the eased 0..1 blend so everything lands in lockstep.
			float campEased = TickCamp((float)deltaTime);
			if (campEased > 0f && _hasCampFocus)
			{
				framingAnchor = framingAnchor.Lerp(_campFocusPoint, campEased);
			}
			orbitDistance = Mathf.Lerp(distance, campDistance, campEased);
			// Only drive the FOV while camp is engaged. Outside camp, _campBaseFov
			// is unset (0) and campEased is 0, so this would lerp Fov to 0 every
			// frame — which Godot rejects (valid FOV is 1..179), spamming
			// set_fov errors. _campActive stays true through the full ease-out, so
			// the final frame (campEased == 0) restores Fov to _campBaseFov before
			// it clears; the preset's FOV is left untouched the rest of the time.
			if (Projection == ProjectionType.Perspective && _campActive)
			{
				Fov = Mathf.Lerp(_campBaseFov, campFov, campEased);
			}

			float pitchRad = Mathf.DegToRad(Mathf.Lerp(pitchDegrees, campPitchDegrees, campEased));
			float yawRad = _yaw;
			if (campEased > 0f)
			{
				// Detuned yaw/pitch sinusoids, scaled by the blend so the wobble
				// fades in with the framing and out on leave.
				_campWobbleTime += (float)deltaTime;
				float w = Mathf.Tau / Mathf.Max(campWobblePeriodSeconds, 0.01f);
				float amp = Mathf.DegToRad(campWobbleAmplitudeDegrees) * campEased;
				yawRad += Mathf.Sin(_campWobbleTime * w) * amp;
				pitchRad += Mathf.Cos(_campWobbleTime * w * 0.7f) * amp * 0.5f;
			}
			GlobalRotation = new Vector3(pitchRad, yawRad, 0);
		}
		GlobalPosition = framingAnchor + GlobalTransform.Basis.Z * orbitDistance;

		// Camera shake offset, applied before the chunky-pixel snap in
		// GameClient._Process so the shake quantizes onto the snap grid
		// rather than fighting it.
		Vector3 shakeOffset = _shake.Tick((float)deltaTime, playerPosition, GlobalBasis);
		if (shakeOffset != Vector3.Zero)
		{
			GlobalPosition += shakeOffset;
		}

		AdvanceClipFade((float)deltaTime);
	}

	// Point the camera at a live subject. The framing anchor eases from the
	// player toward GetFollowTarget(subject) over focusBlendTime, tracking the
	// node each frame while it stays valid. A fallback point is latched now so
	// the framing survives the subject despawning (e.g. a corpse fading out).
	public void FocusOn(Node3D subject)
	{
		_focusNode = subject;
		if (subject != null && GodotObject.IsInstanceValid(subject))
		{
			_focusPoint = GetFollowTarget(subject.GlobalPosition);
			_hasFocusPoint = true;
		}
		_focusing = subject != null;
	}

	// Point the camera at a fixed world point (no live tracking).
	public void FocusOn(Vector3 worldPoint)
	{
		_focusNode = null;
		_focusPoint = GetFollowTarget(worldPoint);
		_hasFocusPoint = true;
		_focusing = true;
	}

	// Ease the framing back to the player. The subject is kept as the lerp
	// source until the blend reaches 0, then dropped in ResolveFocusAnchor.
	public void ClearFocus()
	{
		_focusing = false;
	}

	// Blends the framing anchor between the player follow position and the
	// active focus subject. No-op (returns playerAnchor) whenever idle, so the
	// normal follow is byte-identical when nothing is focused.
	private Vector3 ResolveFocusAnchor(Vector3 playerAnchor, float deltaTime)
	{
		// Track the live node while valid; otherwise fall back to the latched
		// point so a despawned corpse still frames cleanly through the ease-out.
		if (_focusNode != null && GodotObject.IsInstanceValid(_focusNode))
		{
			_focusPoint = GetFollowTarget(_focusNode.GlobalPosition);
			_hasFocusPoint = true;
		}

		float targetBlend = _focusing && _hasFocusPoint ? focusWeight : 0f;
		if (_focusBlend <= 0f && targetBlend <= 0f)
		{
			// Fully released: drop the subject so a stale node isn't held.
			_focusNode = null;
			_hasFocusPoint = false;
			return playerAnchor;
		}

		float blendT = 1f - Mathf.Pow(0.01f, deltaTime / Mathf.Max(0.0001f, focusBlendTime));
		_focusBlend = Mathf.Lerp(_focusBlend, targetBlend, blendT);
		if (_focusBlend < 0.001f && targetBlend <= 0f)
		{
			_focusBlend = 0f;
			_focusNode = null;
			_hasFocusPoint = false;
			return playerAnchor;
		}

		return playerAnchor.Lerp(_focusPoint, _focusBlend);
	}

	// Mirrors the main camera's pose and projection into the off-screen
	// cap-mask camera. Must be called AFTER GameClient's chunky-pixel
	// camera snap so the mask renders with the same snapped pose as the
	// visible scene — otherwise the mask is sub-texel offset from the
	// main render and the cap edges shimmer. Also resizes the mask
	// SubViewport to exactly match the inner pre-upscale viewport so
	// SCREEN_UV samples line up 1:1 with the chunky pixel grid.
	public void SyncCapMaskCamera(Vector2I innerViewportSize)
	{
		// Same init-order guard ApplyClipPlanes uses: GameClient._Process
		// can call this before the cap-mask camera + viewport are wired
		// during init. Skip until they exist; the cap-mask render is
		// purely a visual augmentation and missing one frame on startup
		// is harmless.
		if (_capMaskCamera == null || _capMaskViewport == null)
		{
			return;
		}
		_capMaskCamera.GlobalTransform = GlobalTransform;
		if (Projection == ProjectionType.Perspective)
		{
			_capMaskCamera.Projection = ProjectionType.Perspective;
			_capMaskCamera.Fov = Fov;
		}
		else
		{
			_capMaskCamera.Projection = ProjectionType.Orthogonal;
			_capMaskCamera.Size = Size;
		}
		_capMaskCamera.Near = Near;
		_capMaskCamera.Far = Far;

		var targetSize = new Vector2I(Mathf.Max(1, innerViewportSize.X), Mathf.Max(1, innerViewportSize.Y));
		if (_capMaskViewport.Size != targetSize)
		{
			_capMaskViewport.Size = targetSize;
		}

		// Keep the outline mask camera locked to the main camera so the mask
		// silhouette registers 1:1 with the visible model, and its viewport at
		// the inner (pre-upscale) size so the composite quad's SCREEN_UV maps
		// straight onto the mask texels.
		if (_outlineMaskCamera != null && _outlineMaskViewport != null)
		{
			_outlineMaskCamera.GlobalTransform = GlobalTransform;
			if (Projection == ProjectionType.Perspective)
			{
				_outlineMaskCamera.Projection = ProjectionType.Perspective;
				_outlineMaskCamera.Fov = Fov;
			}
			else
			{
				_outlineMaskCamera.Projection = ProjectionType.Orthogonal;
				_outlineMaskCamera.Size = Size;
			}
			_outlineMaskCamera.Near = Near;
			_outlineMaskCamera.Far = Far;
			if (_outlineMaskViewport.Size != targetSize)
			{
				_outlineMaskViewport.Size = targetSize;
			}
		}
	}

	// Show / hide the fullscreen outline composite quad. Called by
	// InteractiveMeshHighlight when an interactive becomes / stops being the
	// player's highlight target. When hidden the fullscreen pass is skipped
	// entirely (the mask viewport still renders, but it's empty and cheap).
	public void SetOutlineActive(bool active)
	{
		if (_outlineQuad != null)
		{
			_outlineQuad.Visible = active;
		}
	}

	// Commits a base cutaway elevation, resolved by ClipIris from its probe ring.
	//
	// The manual reveal floors it: R3 forces the cutaway down to the plateau above
	// the player's head whatever the world says overhead, so it composes with an
	// automatic cut rather than fighting it.
	public void SetClip(float targetClip, Vector3 playerPos)
	{
		if (_clipAlways)
		{
			float eyeY = playerPos.Y + EYE_HEIGHT;
			float alwaysClip = Mathf.Ceil(eyeY / PLATEAU_STEP) * PLATEAU_STEP - ClipClearance;
			targetClip = Mathf.Min(targetClip, alwaysClip);
		}

		// Stability filter — a new candidate has to match for
		// clipTargetStabilityFrames consecutive frames before it lands
		// in RequestClip. Targets that match what we last committed
		// (_clip or _clipPrev) bypass the wait because they're either a
		// no-op or a reversal, both of which already handle continuity
		// cleanly. Approximate equality (within CLIP_MATCH_EPSILON) treats
		// floating-point jitter from the raycast as the "same" target.
		bool matchesCommitted = NearlyEqualClip(targetClip, _clip) || NearlyEqualClip(targetClip, _clipPrev);
		if (matchesCommitted)
		{
			_candidateTarget = targetClip;
			_candidateTargetFrames = clipTargetStabilityFrames;
			RequestClip(targetClip, playerPos);
			return;
		}

		if (NearlyEqualClip(targetClip, _candidateTarget))
		{
			_candidateTargetFrames++;
		}
		else
		{
			_candidateTarget = targetClip;
			_candidateTargetFrames = 1;
		}

		if (_candidateTargetFrames >= clipTargetStabilityFrames)
		{
			RequestClip(targetClip, playerPos);
		}
	}


	// Approximate equality for clip Y comparisons. Both infinite ⇒ equal;
	// one infinite ⇒ not equal; finite ⇒ within CLIP_MATCH_EPSILON. Used by the
	// stability filter so probe jitter doesn't reset the frame counter.
	private static bool NearlyEqualClip(float a, float b)
	{
		bool aInf = float.IsInfinity(a);
		bool bInf = float.IsInfinity(b);
		if (aInf || bInf)
		{
			return aInf && bInf;
		}
		return Mathf.Abs(a - b) < CLIP_MATCH_EPSILON;
	}

	// Routes a target clip Y through the fade state. Three branches:
	//   1. Already heading there (targetClip == _clip): no-op, just
	//      update the cap-plane center context.
	//   2. Mid-fade and target == _clipPrev: the player turned around
	//      before the current transition finished. Swap _clip/_clipPrev
	//      and flip _clipBlend so the visible dither state stays
	//      mathematically continuous — combined with the shader's phase-
	//      flip on direction reversal, there's no pop. Cheap and matches
	//      "walk in, walk out" intuition.
	//   3. Any other change: idle starts a fresh fade; mid-fade just
	//      updates _clip without touching blend or fadeT. The iris
	//      always runs its full authored duration to completion. The
	//      band shape it operates on can move during the animation —
	//      pixels at the band edge can pop when the edge sweeps past
	//      their Y, but the iris itself never aborts or restarts.
	private void RequestClip(float targetClip, Vector3 centerPos)
	{
		if (targetClip == _clip)
		{
			ApplyClipPlanes(centerPos);
			return;
		}

		bool fading = _clipFadeT < 1f;
		if (fading && targetClip == _clipPrev)
		{
			// Swap endpoints, invert the visible iris position, then
			// back-solve the linear t against the NEW direction's ease
			// curve so subsequent AdvanceClipFade ticks continue smoothly.
			// Without the inverse, _clipFadeT would still be reading off
			// the old (cutting) curve while the easing now applied is the
			// new (revealing) curve — speed would jump and the iris
			// position would skip forward.
			(_clip, _clipPrev) = (_clipPrev, _clip);
			_clipBlend = 1f - _clipBlend;
			bool revealing = _clip > _clipPrev;
			_clipFadeT = EaseClipBlendInverse(_clipBlend, revealing);
			ApplyClipPlanes(centerPos);
			RenderingServer.GlobalShaderParameterSet("camera_clip_growth_center", centerPos);
			PushClipGlobals();
			return;
		}

		if (fading)
		{
			// Mid-fade retarget that isn't a reversal: keep advancing
			// the current iris animation, just shift the destination.
			// Whatever pixel pops occur at the band-edge sweep are the
			// price; the iris must complete its full authored sweep.
			_clip = targetClip;
			ApplyClipPlanes(centerPos);
			RenderingServer.GlobalShaderParameterSet("camera_clip_growth_center", centerPos);
			PushClipGlobals();
			return;
		}

		StartClipFade(targetClip, centerPos);
	}

	private void StartClipFade(float targetClip, Vector3 centerPos)
	{
		_clipPrev = _clip;
		_clip = targetClip;
		_clipBlend = 0f;
		_clipFadeT = 0f;
		ApplyClipPlanes(centerPos);
		// Capture the growth center for the iris-style cutaway dither
		// (see clip_dither.gdshaderinc) at the moment the transition is
		// triggered. The disk is anchored to where the player stood when
		// they crossed the boundary; small drift during the fade keeps
		// the iris stable. Reversal / mid-fade retarget paths in
		// RequestClip also push this so the center always tracks the
		// latest interaction.
		RenderingServer.GlobalShaderParameterSet("camera_clip_growth_center", centerPos);
		PushClipGlobals();
	}

	// Public so the bird's-eye driver (which skips UpdateCamera) can still
	// advance an in-progress clip fade. Without this, SetClip targets sit
	// at their current blend and never land.
	public void AdvanceClipFade(float deltaTime)
	{
		if (_clipFadeT >= 1f)
		{
			return;
		}
		bool revealing = _clip > _clipPrev;
		float duration = revealing ? clipFadeUpSeconds : clipFadeDownSeconds;
		_clipFadeT = Mathf.Min(1f, _clipFadeT + deltaTime / Mathf.Max(duration, 1e-3f));
		_clipBlend = EaseClipBlend(_clipFadeT, revealing);
		if (_clipFadeT >= 1f)
		{
			_clipPrev = _clip;
			_clipBlend = 1f;
		}
		PushClipGlobals();
	}

	// Ease-out curves applied to the linear t-progress before the value
	// gets pushed to camera_clip_blend. Revealing (up) is a gentler
	// quadratic — slows just at the end. Cutting (down) is a stronger
	// cubic — starts fast, slows considerably toward completion. Both
	// satisfy ease(0)=0 and ease(1)=1, so the iris always sweeps the
	// full phase range over its authored duration.
	private static float EaseClipBlend(float t, bool revealing)
	{
		float invT = 1f - t;
		if (revealing)
		{
			return 1f - invT * invT;
		}
		return 1f - invT * invT * invT;
	}

	// Inverse of EaseClipBlend — given a visible iris position v in
	// [0,1], returns the linear t that produced it under the given
	// direction's curve. Used by the reversal path so a mid-fade direction
	// swap can preserve the visible iris position (set _clipBlend = 1-v)
	// while continuing to advance at the new direction's rate.
	private static float EaseClipBlendInverse(float v, bool revealing)
	{
		float invV = Mathf.Max(1f - v, 0f);
		if (revealing)
		{
			return 1f - Mathf.Sqrt(invV);
		}
		return 1f - Mathf.Pow(invV, 1f / 3f);
	}

	private void ApplyClipPlanes(Vector3 centerPos)
	{
		// UpdateClip can fire from GameClient._Process before the cap-plane
		// nodes are wired (the clip-cap mesh + materials are created later
		// in init alongside the SubViewport for the cap mask). Skip in
		// that case; clip globals still propagate to shaders via the
		// PushClipGlobals path.
		if (_clipCapPlane == null || _waterCapPlane == null)
		{
			return;
		}
		// Anchor the cap to MIN(_clip, _clipPrev) so it covers whichever
		// Y the dither is still cutting against during a transition. The
		// "walk out from cover" case is the one that hurts without this:
		// _clip jumps to infinity at trigger time, but the iris ring is
		// still revealing pixels at the OLD lower clip for the full
		// blend window. With the cap pinned to the higher new _clip
		// (i.e. hidden), those still-cut pixels show through to the
		// inside of walls. Holding at the lower of the two keeps the
		// cap below the dither band until the blend completes — at
		// which point AdvanceClipFade sets _clipPrev = _clip and the
		// next-frame re-apply naturally hides the cap.
		float effectiveClip = Mathf.Min(_clip, _clipPrev);
		if (effectiveClip < float.PositiveInfinity)
		{
			_clipCapPlane.Visible = CVars.ceilingCap.Value;
			_clipCapPlane.GlobalPosition = new Vector3(centerPos.X, effectiveClip - capPlaneYBias, centerPos.Z);
			_waterCapPlane.Visible = true;
			_waterCapPlane.GlobalPosition = new Vector3(centerPos.X, effectiveClip - capPlaneYBias, centerPos.Z);
		}
		else
		{
			_clipCapPlane.Visible = false;
			_waterCapPlane.Visible = false;
		}
	}

	private void PushClipGlobals()
	{
		RenderingServer.GlobalShaderParameterSet("camera_clip", _clip);
		RenderingServer.GlobalShaderParameterSet("camera_clip_prev", _clipPrev);
		RenderingServer.GlobalShaderParameterSet("camera_clip_blend", _clipBlend);
	}

	public void RotateLeft()
	{
		_destYaw += Mathf.DegToRad(90);
		_yawStart = _yaw;
		_rotationElapsed = 0f;
		_rotating = true;
		// Positive yaw delta = camera turns left in world → objects sweep
		// right on screen → motion blur trails right.
		_rotationBlur = 1f;
		_rotationBlurDir = Vector2.Right;
	}

	public void RotateRight()
	{
		_destYaw -= Mathf.DegToRad(90);
		_yawStart = _yaw;
		_rotationElapsed = 0f;
		_rotating = true;
		_rotationBlur = 1f;
		_rotationBlurDir = Vector2.Left;
	}

	// Snap the yaw to an absolute angle, dropping any in-flight Q/E tween. For
	// callers that drive yaw continuously (the world editor's right-drag free
	// look) rather than in 90° steps.
	public void SetYaw(float yawRadians)
	{
		_yaw = yawRadians;
		_destYaw = yawRadians;
		_yawStart = yawRadians;
		_rotating = false;
	}

	public void ToggleClipAlways()
	{
		_clipAlways = !_clipAlways;
	}

	// Exposed so GameClient can clear the user-toggled cutaway when entering
	// bird's-eye — the overlook is meant to read open-sky regardless of the
	// indoor toggle state, and we don't want the cutaway to snap back when
	// FlyDown ends if the user had it on at fly-up time.
	public bool ClipAlways
	{
		get => _clipAlways;
		set => _clipAlways = value;
	}


	// Positions the iris disk's cap. Driven every frame rather than from
	// RequestClip: the disk's radius and height move continuously while it grows,
	// with no clip-height change to hang a callback off.
	public void UpdateIrisCap(bool active, float targetY, Vector3 centerPos)
	{
		if (_irisCapPlane == null)
		{
			return;
		}
		bool show = active && CVars.ceilingCap.Value && targetY < float.PositiveInfinity;
		_irisCapPlane.Visible = show;
		if (show)
		{
			_irisCapPlane.GlobalPosition = new Vector3(centerPos.X, targetY - capPlaneYBias, centerPos.Z);
		}
	}
}

// A swappable bundle of camera framing settings for A/B testing angles, selected
// by the camera_preset CVar and pushed into the live camera by
// GameCamera.ApplyAngleSettings only on change — never per frame — so the fields
// it writes stay live-tunable in the editor between swaps.
public struct CameraAngleSettings
{
	public bool Perspective;
	// Vertical FOV in degrees; used only when Perspective is true (orthographic
	// framing is driven by the camera's Size, which presets leave untouched).
	public float Fov;
	public float Distance;
	public float PitchDegrees;
	// Free-look orbit mode (preset 3): mouse / right-stick drive continuous yaw
	// and pitch instead of the fixed pitch + discrete Q/E yaw. Distance still
	// governs the orbit radius; PitchDegrees is only the starting pitch.
	public bool FreeLook;

	// Preset 0 — the current shipping framing. Orthographic, pulled well back
	// and angled shallow. The Fov here only matters if Godot's volumetric fog
	// froxel pipeline (which assumes perspective) needs perspective to render;
	// ~14.25° = 2*atan(20 / 160), matching the ortho view extent at this distance.
	public static readonly CameraAngleSettings Orthographic = new CameraAngleSettings
	{
		Perspective = false,
		Fov = 14.25f,
		Distance = 80f,
		PitchDegrees = -40f,
	};

	// Preset 1 — A/B alternative. Perspective, closer in, steeper pitch.
	public static readonly CameraAngleSettings Perspective70 = new CameraAngleSettings
	{
		Perspective = true,
		Fov = 70f,
		Distance = 18f,
		PitchDegrees = -55f,
	};
	public static readonly CameraAngleSettings Perspective20 = new CameraAngleSettings
	{
		Perspective = true,
		Fov = 20f,
		Distance = 55f,
		PitchDegrees = -40f,
	};

	// Preset 3 — free-look orbit. Same framing as Perspective20 but the mouse /
	// right stick continuously rotate the orbit's yaw and pitch.
	public static readonly CameraAngleSettings FreeLookOrbit = new CameraAngleSettings
	{
		Perspective = true,
		Fov = 20f,
		Distance = 55f,
		PitchDegrees = -40f,
		FreeLook = true,
	};

	public static CameraAngleSettings FromPreset(int index)
	{
		switch (index)
		{
			case 0: return Orthographic;
			case 1: return Perspective70;
			case 2: return Perspective20;
			case 3: return FreeLookOrbit;
			default: return Orthographic;
		}
	}
}
