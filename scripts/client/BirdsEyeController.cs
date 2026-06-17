using Godot;

// Bird's-eye overlook driver. The player fires onBirdsEye(true/false);
// GameClient forwards it to SetActive, which runs a three-phase state machine
// (FlyUp → Steady → FlyDown) that lifts the camera off the player, zooms the
// orthographic Size out, recedes fog, swaps ground ambience for high-altitude
// wind, and streams a wider chunk backdrop behind a fog reveal — then reverses
// on cancel. Motion blur is exposed via MotionBlur and composited by
// ScreenEffectsController. Movement-lock release waits for
// Player.OnBirdsEyeReturnComplete, called when FlyDown lands back at base.
//
// Owns its own camera reference (needed in _Ready to build the cloud quad,
// before GameClient.Current is set); the player / viewport / scene environment
// / hud are read through GameClient.Current during the gameplay methods.
[GlobalClass]
public partial class BirdsEyeController : Node
{
	[Export] public GameCamera camera;

	[ExportGroup("Transition")]
	// Vertical lift (world meters) added to the camera's normal offset at the
	// top of the FlyUp. Orthographic projection so altitude doesn't change
	// scale on its own — paired with the size multiplier below for the zoom.
	[Export(PropertyHint.Range, "0,400,1,or_greater")] public float altitude = 80f;
	// Degrees to steepen (lower) the camera pitch at the apex, eased in
	// alongside the lift and zoom. The camera's resting pitchDegrees is
	// negative (looking down), so we subtract this to tilt further toward
	// straight-down for the overview. Reverses on FlyDown.
	[Export(PropertyHint.Range, "0,45,1,or_greater")] public float pitchDelta = 15f;
	// Multiplier on the camera's base orthographic Size at the apex. This is
	// the "zoom out" knob; combined with altitude it controls how big the
	// overview reads. The overlook streaming profile (ChunkManager
	// BeginOverlook + the fog reveal) fills the wider footprint as the camera
	// zooms, so this can go well past the old 4× — the backdrop chunks beyond
	// the normal load distance stream in visual-only and hide behind the fog
	// curtain until resident.
	[Export(PropertyHint.Range, "1,16,0.25,or_greater")] public float sizeMultiplier = 8f;
	// Wall-clock seconds for either transition (fly-up and fly-down match).
	[Export(PropertyHint.Range, "0.25,5,0.05")] public float transitionSeconds = 1.5f;
	// Peak motion-blur strength during FlyUp. 1 = max (heavy smear), 0 = no
	// blur. Tapers to 0 at the apex via sin(πt) regardless of peak.
	[Export(PropertyHint.Range, "0,1,0.05")] public float motionBlurPeak = 1f;

	[ExportGroup("Fog")]
	// Fog visibility multiplier at the apex. Stretches fog_max_distance and
	// thins both fog densities by 1/scale so the overview isn't smothered
	// by ground-level fog. Lerps from 1 (ground) to this at full lift via
	// SkyController.FogVisibilityScale.
	[Export(PropertyHint.Range, "1,16,0.25,or_greater")] public float fogVisibilityScale = 4f;
	// Apex multiplier on the GENERAL whole-scene haze (ambient_fog_density) for
	// the overview. 0 = no general fog aloft (authored low-lying fog_map volumes
	// are unaffected and stay visible). Eased 1→this over the lift, restored to
	// 1 on the way down.
	[Export(PropertyHint.Range, "0,1,0.05")] public float ambientFogScale = 0f;
	// Apex multiplier on the scene Environment's built-in DEPTH fog range
	// (fog_depth_begin/end). The authored range (~88–105 m, black) is meant for
	// ground-level play; at this multiplier it's pushed well past the camera far
	// plane so the overview shows distant ground instead of a black wall. Eased
	// 1→this over the lift, restored to 1 on the way down.
	[Export(PropertyHint.Range, "1,64,1,or_greater")] public float builtinFogDepthScale = 16f;
	// Time-constant (seconds) for easing the fog-reveal radius out toward the
	// chunk-load frontier. The fly-up timing itself is fixed (transitionSeconds);
	// this only smooths the haze edge so a batch of chunks finishing in one
	// frame doesn't snap the curtain outward. Clamped to never exceed the
	// frontier, so it can't reveal a chunk that isn't resident.
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float revealSmoothTime = 0.3f;

	[ExportGroup("Audio")]
	// Looping high-altitude wind, faded in along the eased lift so the overview
	// crossfades from ground ambience to wind aloft. Lives on a non-ducked bus
	// (Master) so the ambience-duck below doesn't pull it down with the ground
	// layers. Apex volume below.
	[Export] public AudioStreamPlayer windAudio;
	[Export(PropertyHint.Range, "-40,0,1")] public float windVolumeDb = -7f;
	// One-shot whoosh played at the start of the fly-up AND the fly-down.
	[Export] public AudioStreamPlayer swooshAudio;
	// How far the ground ambience bus is ducked at the apex (dB), so the wind
	// reads as taking over aloft. Restored on the way down.
	[Export(PropertyHint.Range, "0,24,0.5")] public float ambienceDuckDb = 12f;

	enum EPhase { None, FlyUp, Steady, FlyDown }
	EPhase _phase = EPhase.None;
	float _elapsed;
	float _baseSize;
	float _blur;
	// Eased fog-reveal radius (world metres). -1 = "snap to the frontier on the
	// next overlook frame" (set on FlyUp start and after FlyDown completes).
	float _revealRadius = -1f;
	// Ground ambience bus name + its pre-overlook volume, captured on FlyUp so
	// the eased duck restores cleanly even if the authored level changes.
	const string AMBIENCE_BUS_NAME = "Ambience2D";
	float _ambienceBaseVolumeDb;
	// Authored built-in depth-fog range, captured on FlyUp so the eased push
	// (and restore) ride the values the Environment was actually authored with.
	float _builtinFogDepthBeginBase;
	float _builtinFogDepthEndBase;
	// Wind volume at eased=0 — effectively silent so the loop starts inaudible.
	const float WIND_DB_FLOOR = -60f;
	// Height above the player's feet treated as the on-ground listener anchor
	// (matches the AudioListener3D's authored local Y in player.tscn).
	const float AUDIO_LISTENER_HEAD_HEIGHT = 1f;

	// Flat plane mesh that renders clouds from above when the camera clears the
	// cloud layer. Parented to the camera at local (0,0,-6) so the mesh follows
	// the camera and stays inside the frustum; visible only while the overview
	// is active AND the `clouds` CVar is enabled. Built in _Ready (which runs
	// before GameClient.Current is set), hence the local camera export.
	MeshInstance3D _cloudOverheadPlane;
	ShaderMaterial _cloudOverheadMaterial;

	// True whenever the overview is engaged (any non-None phase). GameClient's
	// _Process camera-mode switch and teardown read this.
	public bool IsActive => _phase != EPhase.None;
	// True while the camera is overhead (FlyUp rising or Steady at apex) — the
	// window during which GameClient's foliage cutaway must stay contracted.
	public bool IsLifting => _phase == EPhase.FlyUp || _phase == EPhase.Steady;
	// Ceiling the foliage cutaway activation is clamped to while lifting:
	// (1-t)² during the rise (tracks 1 minus the lift's eased curve), 0 once at
	// the apex, so the dithered iris contracts in lockstep with the lift.
	public float FoliageActivationCeiling
	{
		get
		{
			if (_phase == EPhase.FlyUp)
			{
				float duration = Mathf.Max(0.0001f, transitionSeconds);
				float t = Mathf.Min(1f, _elapsed / duration);
				return (1f - t) * (1f - t);
			}
			return 0f;
		}
	}
	// FlyUp motion-blur strength (0 outside FlyUp), composited by
	// ScreenEffectsController via GameClient.
	public float MotionBlur => _blur;

	public override void _Ready()
	{
		// 2D screen-space cloud quad — fullscreen NDC pass that samples two
		// noise reads per pixel (base offset + coverage) to render a cloud
		// layer bounded to [50%, 75%] of player→camera height. Same shape
		// as the in-scene FogQuad: a QuadMesh at 2×2, parented to the camera
		// at local (0,0,-6) so the mesh follows the camera and stays inside
		// the frustum. The shader emits POSITION = (VERTEX.xy * 2, 1, 1) so
		// the world transform doesn't matter for fragment placement — only
		// for AABB-based culling. Cost is bounded to the overlook scene by
		// the Visible gate.
		if (camera == null) { return; }
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
	}

	public override void _ExitTree()
	{
		// Safety net: a quit or scene change mid-overlook never runs the FlyDown
		// teardown, so the globally-shared ground ambience bus would stay ducked
		// — and the next overlook would snapshot that ducked level as its base,
		// accumulating the duck across runs. Undo it here if still active.
		if (_phase != EPhase.None)
		{
			int ambBus = AudioServer.GetBusIndex(AMBIENCE_BUS_NAME);
			if (ambBus >= 0)
			{
				AudioServer.SetBusVolumeDb(ambBus, _ambienceBaseVolumeDb);
			}
		}
	}

	// Player.onBirdsEye bridge. active=true starts the fly-up; active=false
	// reverses into the fly-down (no-op if not currently active).
	public void SetActive(bool active)
	{
		Player player = GameClient.Current?.Player;
		if (player == null) { return; }
		if (active)
		{
			// Capture the resting ortho Size so the fly-up zoom and the fly-down
			// snap-back both lerp against the same anchor — CVar tweaks to the
			// base size during the overlook don't strand the camera zoomed in.
			_baseSize = camera.Size;
			_phase = EPhase.FlyUp;
			_elapsed = 0f;
			// Switch the chunk streamer into its panorama profile and arm the
			// fog reveal to snap to the frontier on its first frame (so the
			// already-loaded near field isn't briefly curtained). The streamed
			// radius is sized from the apex visible footprint so the backdrop
			// reaches the screen corners even at extreme zoom (otherwise the
			// reveal — clamped to the loaded frontier — leaves a foggy ring).
			World.Current?.ChunkManager?.BeginOverlook(ComputeOverlookGroundRadius());
			_revealRadius = -1f;
			camera.ManualClipMode = true;
			// Force the indoor cutaway off so the camera can see the world from
			// above even if the player started under a roof. SetClip routes
			// through the existing fade so the ceiling cap dissolves smoothly.
			// ClipAlways=false drops the user-toggled cutaway too, and stays
			// false after FlyDown so the player has to re-enable it manually
			// if they want it back.
			camera.ClipAlways = false;
			camera.SetClip(float.PositiveInfinity, player.GlobalPosition);
			// Cloud quad renders only while the overview is active AND the
			// `clouds` CVar is enabled. UpdateCamera re-checks the CVar every
			// frame so toggling it mid-overlook updates live.
			if (_cloudOverheadPlane != null)
			{
				_cloudOverheadPlane.Visible = CVars.clouds.Value;
			}
			// Audio: whoosh the lift, start the (silent) high-altitude wind loop
			// that UpdateCamera fades in, and snapshot the ground ambience bus
			// level so the eased duck restores to it on the descent.
			swooshAudio?.Play();
			if (windAudio != null)
			{
				windAudio.VolumeDb = WIND_DB_FLOOR;
				windAudio.Play();
			}
			int ambBusUp = AudioServer.GetBusIndex(AMBIENCE_BUS_NAME);
			if (ambBusUp >= 0)
			{
				_ambienceBaseVolumeDb = AudioServer.GetBusVolumeDb(ambBusUp);
			}
			// Snapshot the authored built-in depth-fog range so the eased push
			// (and the restore on completion) stay relative to it.
			Godot.Environment env = GameClient.Current?.sceneEnvironment?.Environment;
			if (env != null)
			{
				_builtinFogDepthBeginBase = env.FogDepthBegin;
				_builtinFogDepthEndBase = env.FogDepthEnd;
			}
			// Strip the in-world UI for the clean overview shot: HUD, dust motes,
			// the interactive outline + its floating prompt. The per-frame
			// highlight gate and UpdateInteractHUD keep them from reappearing
			// while the overview is active.
			GameClient.Current?.SetBirdsEyeUiHidden(true);
		}
		else
		{
			if (_phase == EPhase.None)
			{
				return;
			}
			// Whoosh the descent too (the climb back down past the wind).
			swooshAudio?.Play();
			// Cancelling mid-FlyUp: seed _elapsed so FlyDown picks up at the
			// current eased height instead of snapping. FlyUp eases toward the
			// apex as 1-(1-t)^2; FlyDown eases toward the ground as t^2 (t runs
			// 1→0). Solve t^2 = easedNow for the FlyDown start fraction so the
			// height is continuous. From Steady (easedNow=1) this lands on
			// elapsed=0 naturally.
			float duration = Mathf.Max(0.0001f, transitionSeconds);
			float easedNow = 1f;
			if (_phase == EPhase.FlyUp)
			{
				float currentT = Mathf.Clamp(_elapsed / duration, 0f, 1f);
				easedNow = 1f - (1f - currentT) * (1f - currentT);
			}
			// FlyDown's per-frame t = 1 - elapsed/duration, so to start at
			// t = sqrt(easedNow) we seed elapsed = (1 - sqrt(easedNow))·duration.
			float startT = Mathf.Sqrt(easedNow);
			_phase = EPhase.FlyDown;
			_elapsed = (1f - startT) * duration;
		}
	}

	// Per-frame camera drive while the overview is active. Owns position (lifted
	// along world-up off the player), ortho Size (zoom), fog recession, audio
	// crossfade, and the cloud band. GameClient calls this, then its own
	// SnapCameraAndUpdateUpscale + ApplySpriteChunky + CullProps. End-of-FlyDown
	// signals Player.OnBirdsEyeReturnComplete to drop the movement lock.
	public void UpdateCamera(double deltaTime)
	{
		Player player = GameClient.Current?.Player;
		if (player == null) { return; }
		float dt = (float)deltaTime;
		_elapsed += dt;

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

		float duration = Mathf.Max(0.0001f, transitionSeconds);
		float t;
		bool finished = false;
		if (_phase == EPhase.FlyUp)
		{
			t = Mathf.Min(1f, _elapsed / duration);
			if (t >= 1f)
			{
				_phase = EPhase.Steady;
			}
		}
		else if (_phase == EPhase.FlyDown)
		{
			t = 1f - Mathf.Min(1f, _elapsed / duration);
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
		// FlyDown's start elapsed is seeded in SetActive to keep the eased
		// height continuous across a mid-fly-up cancel.
		float eased = _phase == EPhase.FlyDown
			? t * t                       // ease-out toward the ground (t: 1→0)
			: 1f - (1f - t) * (1f - t);   // ease-out toward the apex  (t: 0→1)

		// Camera pose. Read live camera.Yaw (TickRotation just updated it) so
		// CameraLeft / CameraRight rotate the overview, lift straight up along
		// world-Y by the eased altitude, and steepen the pitch by the eased
		// delta so the overview looks further down. Horizontal tracking stays
		// glued to the player so a knockback (which bypasses the movement lock)
		// doesn't strand the view.
		float pitchDeg = Mathf.Lerp(camera.pitchDegrees, camera.pitchDegrees - pitchDelta, eased);
		float pitch = Mathf.DegToRad(pitchDeg);
		camera.GlobalRotation = new Vector3(pitch, camera.Yaw, 0f);
		Vector3 baseOffset = camera.GlobalTransform.Basis.Z * camera.distance;
		Vector3 lifted = baseOffset + Vector3.Up * altitude;
		// Anchor on the SAME framing target the normal follow uses, not the raw
		// player feet — otherwise the eased=0 endpoint sits ~followHeightOffset
		// below where the standard camera path resumes, popping the view on the
		// FlyDown handoff.
		Vector3 followTarget = camera.GetFollowTarget(player.GlobalPosition);
		camera.GlobalPosition = followTarget + baseOffset.Lerp(lifted, eased);

		camera.Size = Mathf.Lerp(_baseSize, _baseSize * sizeMultiplier, eased);

		// Push fog visibility along the same eased curve so the overview clears
		// in step with the lift. SkyController reads this every frame; on
		// FlyDown completion we reset it to 1.0 below so ground-level fog
		// resumes its normal range. Null-safe: SkyController is created during
		// scene init and outlives GameClient, but the static singleton may
		// briefly be unset during teardown.
		if (SkyController.Current != null)
		{
			SkyController.Current.FogVisibilityScale = Mathf.Lerp(1f, fogVisibilityScale, eased);
			// Suppress only the general haze; authored fog_map volumes stay full.
			SkyController.Current.AmbientFogScale = Mathf.Lerp(1f, ambientFogScale, eased);
		}

		// Recede the scene Environment's built-in DEPTH fog along the same eased
		// curve. This is a SEPARATE fog system from the custom volumetric pass
		// (SkyController only drives the latter); its authored ~88–105 m black
		// wall would otherwise black out all distant ground in the overview.
		Godot.Environment fogEnv = GameClient.Current?.sceneEnvironment?.Environment;
		if (fogEnv != null)
		{
			float depthScale = Mathf.Lerp(1f, builtinFogDepthScale, eased);
			fogEnv.FogDepthBegin = _builtinFogDepthBeginBase * depthScale;
			fogEnv.FogDepthEnd = _builtinFogDepthEndBase * depthScale;
		}

		// Drive the overlook fog reveal: hide everything beyond the streaming
		// frontier so chunks still meshing under the overview are masked by
		// haze. The radius eases out toward the frontier but is clamped to never
		// exceed it (a chunk that isn't resident is never revealed); the soft
		// edge (overlook_reveal_softness) feathers the boundary.
		ChunkManager chunkManager = World.Current?.ChunkManager;
		if (chunkManager != null)
		{
			float frontier = chunkManager.OverlookLoadedRadiusWorld;
			if (_revealRadius < 0f || frontier < _revealRadius)
			{
				// First overlook frame, or the frontier receded — snap (the
				// snap-down case keeps the curtain from ever over-revealing).
				_revealRadius = frontier;
			}
			else
			{
				float revealT = 1f - Mathf.Pow(0.01f, dt / Mathf.Max(0.0001f, revealSmoothTime));
				_revealRadius = Mathf.Min(frontier, Mathf.Lerp(_revealRadius, frontier, revealT));
			}
			Vector3 revealCenter = camera.GetFollowTarget(player.GlobalPosition);
			RenderingServer.GlobalShaderParameterSet("overlook_reveal_center", revealCenter);
			RenderingServer.GlobalShaderParameterSet("overlook_reveal_radius", _revealRadius);
		}

		// Audio rides the same eased curve as the lift:
		//   - the listener climbs from the player's head toward the camera, so
		//     World3D positional audio attenuates with altitude;
		//   - the ground ambience bus ducks while the high-altitude wind fades
		//     in — together a crossfade from ground sounds to wind aloft.
		Vector3 listenerHead = player.GlobalPosition + Vector3.Up * AUDIO_LISTENER_HEAD_HEIGHT;
		player.SetAudioListenerWorldOverride(listenerHead.Lerp(camera.GlobalPosition, eased));
		if (windAudio != null)
		{
			windAudio.VolumeDb = Mathf.Lerp(WIND_DB_FLOOR, windVolumeDb, eased);
		}
		int ambBus = AudioServer.GetBusIndex(AMBIENCE_BUS_NAME);
		if (ambBus >= 0)
		{
			AudioServer.SetBusVolumeDb(ambBus, _ambienceBaseVolumeDb - ambienceDuckDb * eased);
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
			float playerY = player.GlobalPosition.Y;
			float apexCameraY = playerY + lifted.Y;
			float deltaY = apexCameraY - playerY;
			float thickness = 0.25f * deltaY;
			// Camera Y at eased=t — typically constant when altitude=0.
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
		_blur = _phase == EPhase.FlyUp ? Mathf.Sin(t * Mathf.Pi) * motionBlurPeak : 0f;

		if (finished)
		{
			_phase = EPhase.None;
			_blur = 0f;
			camera.Size = _baseSize;
			camera.ManualClipMode = false;
			// Drop the panorama streaming profile (its backdrop ring unloads as
			// the desired set shrinks) and disable the fog curtain.
			World.Current?.ChunkManager?.EndOverlook();
			RenderingServer.GlobalShaderParameterSet("overlook_reveal_radius", 1e20f);
			_revealRadius = -1f;
			if (SkyController.Current != null)
			{
				SkyController.Current.FogVisibilityScale = 1f;
				SkyController.Current.AmbientFogScale = 1f;
			}
			// Restore the built-in depth-fog range to its authored values.
			Godot.Environment doneEnv = GameClient.Current?.sceneEnvironment?.Environment;
			if (doneEnv != null)
			{
				doneEnv.FogDepthBegin = _builtinFogDepthBeginBase;
				doneEnv.FogDepthEnd = _builtinFogDepthEndBase;
			}
			// Restore the HUD + motes hidden for the overview.
			GameClient.Current?.SetBirdsEyeUiHidden(false);
			// Re-seat the follow position so the normal camera path picks up
			// from the player on the next frame rather than lerping from the
			// stale (lifted) follow target.
			camera.SetInitialPosition(player.GlobalPosition);
			if (_cloudOverheadPlane != null)
			{
				_cloudOverheadPlane.Visible = false;
			}
			// Restore audio: listener back to the player's head, wind loop off,
			// ground ambience bus back to its pre-overlook level. eased is 0 at
			// FlyDown completion, so these all landed at their rest values on the
			// final frame already; this makes the reset explicit and stops the
			// looping stream.
			player.SetAudioListenerWorldOverride(null);
			windAudio?.Stop();
			int ambBusDone = AudioServer.GetBusIndex(AMBIENCE_BUS_NAME);
			if (ambBusDone >= 0)
			{
				AudioServer.SetBusVolumeDb(ambBusDone, _ambienceBaseVolumeDb);
			}
			player.OnBirdsEyeReturnComplete();
		}
	}

	// Pushes `sprite_chunky` to the pre-zoom base value so sprite-billboard
	// world size doesn't track the inflated bird's-eye ortho Size. Must run
	// AFTER GameClient.SnapCameraAndUpdateUpscale (which sets the live-Size
	// value); the _Process bird's-eye branch calls it in that order. Skipped
	// (no-op) when the viewport isn't yet wired so the first-frame init path
	// is safe.
	public void ApplySpriteChunky()
	{
		SubViewport sceneViewport = GameClient.Current?.sceneViewport;
		if (sceneViewport == null)
		{
			return;
		}
		float baseChunky = _baseSize / Mathf.Max(1, sceneViewport.Size.Y);
		RenderingServer.GlobalShaderParameterSet("sprite_chunky", baseChunky);
	}

	// Ground radius (metres) from the player to the farthest visible screen
	// corner at the bird's-eye apex. The chunk streamer uses this to size the
	// overlook backdrop so the fog reveal can reach the corners. Orthographic,
	// so altitude doesn't change the footprint — only the apex ortho Size and
	// pitch do. Derivation: the vertical screen axis projects onto the ground
	// foreshortened by 1/sin(pitch-below-horizontal); the horizontal axis is
	// parallel to the ground (no foreshortening). Corner = hypot of the two
	// half-extents. Reads _baseSize, so call after it's captured.
	float ComputeOverlookGroundRadius()
	{
		SubViewport sceneViewport = GameClient.Current?.sceneViewport;
		float apexSize = _baseSize * sizeMultiplier;
		float phi = Mathf.DegToRad(Mathf.Abs(camera.pitchDegrees - pitchDelta));
		float sinPhi = Mathf.Max(0.1f, Mathf.Sin(phi));
		float aspect = sceneViewport != null && sceneViewport.Size.Y > 0
			? (float)sceneViewport.Size.X / sceneViewport.Size.Y
			: 16f / 9f;
		float halfDepth = apexSize * 0.5f / sinPhi;
		float halfWidth = apexSize * aspect * 0.5f;
		return Mathf.Sqrt(halfDepth * halfDepth + halfWidth * halfWidth);
	}
}
