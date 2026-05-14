using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class GameClient : Node3D
{
	public static GameClient Current { get; private set; }

	[Export] public GameCamera camera;
	[Export] public Hud hud;
	[Export] public AlmanacScreen almanacScreen;
	[Export] public CookingScreen cookingScreen;
	[Export] public Node worldHUD;
	[Export] public SubViewport sceneViewport;
	[Export] public MeshInstance3D bloomQuad;
	[Export] public ShaderMaterial upscaleMaterial;
	[Export] public ShaderMaterial fogMaterial;
	[Export] public PackedScene hudTextScene;
	[Export] public PackedScene interactHudScene;
	// Shared world-pickup scene. Every dropped or spawned item materializes
	// through this one scene with its sprite swapped to the item's
	// worldSprite on spawn. The Loot runtime decides per-player whether to
	// auto-pickup (walk over) or require interact based on inventory state.
	[Export] public PackedScene lootScene;
	[Export] public ShaderMaterial outlineMaterial;
	// Flat-sprite outline variant. Used when ApplyHighlight is wrapping a
	// FlatLitSprite — the upright outline shader's vertex math would build
	// a Y-aligned billboard outline that misses the flat geometry by 90°.
	[Export] public ShaderMaterial outlineFlatMaterial;
	[Export] public ShaderMaterial postProcessMaterial;
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

	// Sample wind speed in m/s at `worldPos`. Returns 0 when an upward
	// raycast hits environment geometry — a stand-in for "the player is in
	// a cave or under a roof", where the open-sky wind from the weather
	// system shouldn't reach them. Same shape as SampleAirTemperature so
	// callers can ignore wind whenever they ignore weather.
	public float SampleWindSpeed(Vector3 worldPos)
	{
		SkyController sky = SkyController.Current;
		if (sky?.Weather == null) { return 0f; }
		float wind = sky.Weather.windSpeed;
		if (wind <= 0f) { return 0f; }

		World3D world3D = GetWorld3D();
		if (world3D != null)
		{
			Vector3 from = worldPos + Vector3.Up * 0.1f;
			Vector3 to = from + Vector3.Up * 200f;
			using var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Environment);
			query.CollideWithBodies = true;
			query.CollideWithAreas = false;
			if (_player != null)
			{
				query.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
			}
			if (world3D.DirectSpaceState.IntersectRay(query).Count > 0)
			{
				return 0f;
			}
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
			int px = Mathf.FloorToInt(worldPos.X);
			int py = Mathf.FloorToInt(worldPos.Y);
			int pz = Mathf.FloorToInt(worldPos.Z);
			int sunBfs = ws.GetSunlightWorld(px, py, pz);
			s.sunMask = Mathf.Clamp((float)sunBfs / LightEngine.MAX_LIGHT, 0f, 1f);
		}
		return s;
	}

	public Action onInit;
	public Action<Player> onPlayerSpawned;
	public Action<Vector3, string, ulong, float, Color> onHudText;
	// Multi-line typewriter dialogue. Fired by Mob.Speak when a Talk
	// interaction completes; OnDialogueRequested forwards to the HUD's
	// DialogueController which handles typing, ui_accept advance/skip, and
	// player-input suppression while open.
	public Action<IReadOnlyList<string>> onDialogue;
	public Action<bool> onPauseToggled;
	public Action onQuitToMenu;

	// Fired when the player enters a named region (CurrentRegion null →
	// non-null OR → a different non-null region). Border chunks (RegionIndex
	// points at a Regions[] entry whose Data is null) keep CurrentRegion
	// sticky; clearing back to null on extended border travel is silent so
	// the next named region's entry pulses the banner cleanly.
	public Action<RegionData> onRegionEntered;
	public RegionData CurrentRegion { get; private set; }

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
	// Single gate that any input-consuming modal (signpost, map, inventory)
	// flips when it opens and clears when it closes. Players sees this and
	// skips ProcessInput; _UnhandledInput sees it and drops gameplay input.
	// World.Tick keeps running regardless so the runner can still advance a
	// consumable-use action started from the inventory screen.
	public bool InputSuppressed { get; set; } = false;
	public Player Player => _player;
	public World World => _world;

	Player _player;
	World _world;
	Vector2 _mousePosition;
	Sprite3D _highlightOverlay;
	InteractHUD _interactHUD;
	Vector2 _subpixelTexelOffset;

	const float FLYCAM_SPEED = 20f;
	const float FLYCAM_BOOST = 5f;
	const float FLYCAM_LOOK_SENSITIVITY = 0.005f;
	float _flyYaw;
	float _flyPitch;
	bool _flyInitialized;

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
		InputSuppressed = false;

		GetTree().Root.SizeChanged += UpdateViewportSize;
		UpdateViewportSize();

		if (upscaleMaterial != null)
		{
			upscaleMaterial.SetShaderParameter("inner_tex", sceneViewport.GetTexture());
		}
	}

	public async void Init(Vector3 playerPosition, PackedScene playerScene, PlayerSpawnData playerSpawnData, WorldState worldState)
	{
		onHudText += OnHudTextRequested;
		onDialogue += OnDialogueRequested;
		onInit?.Invoke();

		_world = new World();
		_world.onMobSpawned += OnMobSpawned;
		_world.onMobRemoved += OnMobRemoved;
		_world.onDiscoverableSpawned += OnDiscoverableSpawned;
		sceneViewport.AddChild(_world);
		_world.Initialize(worldState, playerPosition, camera, fogMaterial, () => _player?.GlobalPosition ?? playerPosition);

		while (!_world.IsSpawnChunkReady(playerPosition))
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}

		_player = playerScene.Instantiate<Player>();
		_player.onHighlightChanged += OnPlayerHighlightChanged;
		_player.onInteractChanged += OnPlayerInteractChanged;
		sceneViewport.AddChild(_player);
		_player.Initialize(_world, playerSpawnData, playerPosition, Vector3.Zero);

		_world.SetPlayer(_player);

		camera.Init(sceneViewport);
		camera.SetInitialPosition(_player.GlobalPosition);

		onPlayerSpawned?.Invoke(_player);
	}

	// Push radius and bend strength for the detail-sprite shader's player
	// reaction. ~0.6m matches the player's foot footprint; 0.25m bend reads
	// as grass parting around the player's legs without snapping flat.
	private const float DETAIL_PLAYER_RADIUS = 0.6f;
	private const float DETAIL_PLAYER_STRENGTH = 0.25f;

	public override void _Process(double deltaTime)
	{
		if (_player == null || ConsoleUI.IsOpen || paused)
		{
			return;
		}
		_world.Tick(deltaTime);
		UpdateRegion(deltaTime);

		// Signpost panel shares the Interact key with the gameplay press that
		// opens it. The close has to be caught here (before ProcessInput) so
		// the same press doesn't fall through and immediately retrigger an
		// interaction via _highlightInteractive. Other modals close on
		// ui_cancel in their own _UnhandledInput, which has no such conflict.
		if (hud != null && hud.IsSignpostOpen && Input.IsActionJustPressed("Interact"))
		{
			hud.CloseSignpost();
		}
		else if (!InputSuppressed)
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

		// Per-frame push to the detail_sprite shader so grass bends around
		// the player. Single global, sub-byte cost; written every frame so
		// stale values don't persist when the player teleports.
		RenderingServer.GlobalShaderParameterSet("player_pos", _player.GlobalPosition);
		RenderingServer.GlobalShaderParameterSet("player_radius", DETAIL_PLAYER_RADIUS);
		RenderingServer.GlobalShaderParameterSet("player_strength", DETAIL_PLAYER_STRENGTH);

		if (CVars.debugFlyCam.Value)
		{
			UpdateFlyCamera(deltaTime);
			CullProps(float.PositiveInfinity);
		}
		else
		{
			_flyInitialized = false;
			camera.UpdateCamera(deltaTime, _player.GlobalPosition);
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
		}
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

	void UpdatePostProcess()
	{
		if (postProcessMaterial != null)
		{
			postProcessMaterial.SetShaderParameter("vignette_radius", CVars.vignetteRadius.Value);
			postProcessMaterial.SetShaderParameter("vignette_softness", CVars.vignetteSoftness.Value);
			postProcessMaterial.SetShaderParameter("vignette_strength", CVars.vignetteStrength.Value);
		}
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
			// radius so the cursor lives on a disk. Direction-only after that
			// — atan2 in Player ignores magnitude. The deadzone keeps the
			// resting direction stable when only sub-pixel jitter is arriving.
			_mousePosition += mouseMotion.Relative * CVars.mouseSensitivity.Value;
			if (_mousePosition.LengthSquared() > AIM_CURSOR_RADIUS_PX * AIM_CURSOR_RADIUS_PX)
			{
				_mousePosition = _mousePosition.Normalized() * AIM_CURSOR_RADIUS_PX;
			}
			if (_mousePosition.LengthSquared() >= AIM_CURSOR_DEADZONE_PX * AIM_CURSOR_DEADZONE_PX)
			{
				_player.ProcessMouseMotion(_mousePosition, camera.Yaw);
			}
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		base._UnhandledInput(e);

		if (e.IsActionPressed("TogglePause"))
		{
			TogglePause();
			GetViewport().SetInputAsHandled();
			return;
		}

		// While paused, or while any input-consuming modal is up, gameplay
		// input is dropped. Modal-close keys (ui_cancel for map/inventory,
		// Interact for signpost) fall through to the modal itself in its
		// own _UnhandledInput or to GameClient._Process — see InputSuppressed
		// gate below.
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

	void ApplyHighlight(Node3D node)
	{
		Sprite3D source = FindChildSprite(node);
		if (source == null || !source.Visible)
		{
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
		_highlightOverlay.Visible = false;
		_highlightOverlay.Reparent(sceneViewport, false);
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

	void OnHudTextRequested(Vector3 position, string text, ulong fadeMs, float verticalMovement, Color color)
	{
		HudText.Create(hudTextScene, _world, camera, position, text, fadeMs, verticalMovement, color, this);
	}

	void OnDialogueRequested(IReadOnlyList<string> lines)
	{
		hud?.ShowDialogue(lines);
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

	public void Save()
	{
		SaveGame.Save(CVars.savePath.Value);
	}

	public void QuitToMenu()
	{
		onQuitToMenu?.Invoke();
	}

}
