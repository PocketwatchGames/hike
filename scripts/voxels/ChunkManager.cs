using System;
using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class ChunkManager : Node3D
{
    // Always-loaded sphere around the player. Sized to cover the camera's reach so
    // world-space entity spawning (World.ENTITY_LOAD_RADIUS) can rely on chunks
    // being present regardless of camera angle — rotating the camera must not be
    // able to reveal un-spawned mobs/props.
    private const int NEARBY_RADIUS = 6;
    private const int NEARBY_RADIUS_SQ = NEARBY_RADIUS * NEARBY_RADIUS;
    // Chunks within this distSq always load uncapped, ignoring the sphere
    // cap. Covers the 3x3x3 box around the player (corner distSq = 3) — the
    // chunks they could step into in a single frame. Without this guarantee
    // the player can walk off the edge of their loaded chunk into an
    // adjacent unloaded one and fall through the world. The cost is bounded:
    // 26 chunks max × ~14ms ChunkMesh.Create ≈ 380ms worst-case synchronous
    // hitch when entering fully-uncovered territory, which is acceptable
    // for an "entering new area" stutter.
    private const int NEAR_LOAD_RADIUS_SQ = 3;
    private const int MAX_LOAD_DISTANCE = 10;
    // Player-centric window for the five ImageTexture3D volume maps (light, sky
    // exposure, fog, wind, water current). Diameter must STRICTLY exceed the
    // resident diameter (2*MAX_LOAD_DISTANCE+1 = 21 chunks) so the toroidal
    // texel-wrap boundary never lands inside the rendered region — see
    // WindowedVolumeMap. The +2 margin (one chunk each side) provides that.
    private const int LIGHT_WINDOW_DIAMETER_CHUNKS = MAX_LOAD_DISTANCE * 2 + 1 + 2;
    private const int MAX_REBUILDS_PER_FRAME = 3;
    // Per-frame caps on chunk-mesh generation. ChunkMesh.Create costs ~14ms
    // per chunk in MesherDC; without these caps the hitch detector was
    // catching frames where 26+ chunks meshed synchronously (380ms spikes).
    //   FRUSTUM: visual-only chunks past NEARBY_RADIUS. Capped at 1/frame —
    //     anything higher pushes the frame past the 50ms hitch floor on its
    //     own, even with no other work.
    //   SPHERE:  collision-critical chunks within NEARBY_RADIUS. Capped at
    //     4/frame normally. At 60fps that fills a new sphere face (~113
    //     chunks at the boundary) in <2s, fast enough to outrun normal
    //     movement given the 96m sphere cushion. Initial-load bypass below
    //     handles the bigger pre-spawn fill.
    private const int MAX_FRUSTUM_LOADS_PER_FRAME = 2;
    private const int MAX_SPHERE_LOADS_PER_FRAME = 4;
    private const int MAX_LOAD_DISTANCE_SQ = MAX_LOAD_DISTANCE * MAX_LOAD_DISTANCE;
    // Bird's-eye overlook panorama. While BeginOverlook() is active the frustum
    // pass extends far past MAX_LOAD_DISTANCE in the horizontal plane (the
    // zoomed-out ortho overview reveals hundreds of metres) but stays shallow
    // in Y — only surface chunks matter for a top-down backdrop, so a tall
    // column of vertical chunks would be wasted work. The extra ring loads
    // WITHOUT collision or detail scatter (see LoadChunkInternal) and unloads
    // as soon as the overview ends and the desired set shrinks back inside
    // MAX_LOAD_DISTANCE. The per-frame cap is higher than the normal frustum
    // cap because the backdrop chunks are visual-only (cheaper than a full
    // collision+detail chunk) and the fill happens under a cinematic where a
    // mild framerate dip is acceptable.
    // Default/min overlook horizontal radius (chunks). BeginOverlook widens
    // this to cover the actual zoomed-out footprint the camera reveals — the
    // overview's visible ground radius can exceed the default at high zoom, and
    // a fixed radius would leave the screen corners curtained behind fog (the
    // reveal radius is clamped to the loaded frontier). MAX caps the streaming
    // cost: each backdrop chunk is visual-only but still a mesh build, so the
    // cylinder can't grow without bound.
    private const int OVERLOOK_LOAD_DISTANCE_MIN = 24;
    private const int OVERLOOK_LOAD_DISTANCE_MAX = 48;
    private const int OVERLOOK_Y_BAND = 4;
    private const int MAX_OVERLOOK_LOADS_PER_FRAME = 6;
    // Per-frame cap on chunk unloads. QueueFree() looks cheap synchronously
    // but each ChunkMesh holds a trimesh StaticBody3D (Jolt broadphase entry)
    // + GPU mesh resources + child entity nodes; releasing them happens in
    // the end-of-frame deferred-free batch, which is NOT in TimeProcess and
    // shows up as gap_ms in the hitch detector. Crossing a chunk boundary
    // can queue dozens of unloads at once — capping spreads that deferred
    // batch across frames.
    private const int MAX_UNLOADS_PER_FRAME = 4;

    public event Action<Vector3I> onChunkLoaded;
    public event Action<Vector3I> onChunkUnloaded;

    private readonly Dictionary<Vector3I, ChunkMesh> _loadedChunks = new();
    private readonly Queue<Vector3I> _meshRebuildQueue = new();

    // Bird's-eye overlook streaming state. _overlookActive widens the frustum
    // pass to the panorama radius; _overlookFrontierRadiusWorld is the world
    // radius around the player inside which every desired overlook chunk is
    // resident — the guaranteed-filled frontier. GameClient reads it to drive
    // the fog reveal so the haze edge rides the streaming frontier. Defaults
    // huge so a non-overlook query never triggers the curtain.
    private bool _overlookActive;
    private float _overlookFrontierRadiusWorld = 1e20f;
    // Horizontal overlook radius in chunks, sized per-overlook from the visible
    // ground footprint passed to BeginOverlook so the streamed backdrop always
    // reaches past the screen corners (the fog reveal can only uncover resident
    // chunks). Defaults to the min when an overlook starts without a radius.
    private int _overlookLoadDistance = OVERLOOK_LOAD_DISTANCE_MIN;
    public bool OverlookActive => _overlookActive;
    public float OverlookLoadedRadiusWorld => _overlookFrontierRadiusWorld;
    // worldRadius is the overview's visible ground radius (metres) from the
    // player to the farthest screen corner; the streamed cylinder is grown to
    // cover it (plus a chunk of margin for the frontier shrink + reveal
    // softness), clamped to [MIN, MAX].
    public void BeginOverlook(float worldRadius)
    {
        int chunks = Mathf.CeilToInt(worldRadius / ChunkState.SIZE) + 2;
        _overlookLoadDistance = Mathf.Clamp(chunks, OVERLOOK_LOAD_DISTANCE_MIN, OVERLOOK_LOAD_DISTANCE_MAX);
        _overlookActive = true;
    }
    public void EndOverlook()
    {
        _overlookActive = false;
        _overlookFrontierRadiusWorld = 1e20f;
        _overlookLoadDistance = OVERLOOK_LOAD_DISTANCE_MIN;
    }

    // Scratch buffers reused every UpdateLoadedChunks call. The set fills with
    // ~1100 sphere coords + frustum-visible chunks (thousands per frame); the
    // list collects evictions. Allocating both fresh every frame was a steady
    // contributor to gen0 GC pressure flagged by the hitch detector.
    private readonly HashSet<Vector3I> _desiredScratch = new();
    private readonly List<Vector3I> _toRemoveScratch = new();
    // Distance-sorted load queue. Without sorting, HashSet iteration order is
    // hash-based — random with respect to player position — so with a load
    // cap of 1-2 per frame the chunk that actually loads is often a distant
    // one while a closer (more visible) chunk waits. Sorting by distSq from
    // the player guarantees the closest unloaded chunk loads first each
    // frame, so visible empty space at the camera-near edge fills before
    // far-away chunks the player can barely see.
    private readonly List<Vector3I> _loadCandidatesScratch = new();

    // Static comparison delegate used by _loadCandidatesScratch.Sort. Caches
    // _sortRefCoord (the player's chunk) in a static field so the lambda
    // doesn't allocate a closure each frame. Single-threaded, set just
    // before each Sort call.
    private static Vector3I _sortRefCoord;
    private static readonly Comparison<Vector3I> _byDistSqFromRef = (a, b) =>
    {
        Vector3I ra = a - _sortRefCoord;
        Vector3I rb = b - _sortRefCoord;
        int da = ra.X * ra.X + ra.Y * ra.Y + ra.Z * ra.Z;
        int db = rb.X * rb.X + rb.Y * rb.Y + rb.Z * rb.Z;
        return da.CompareTo(db);
    };

    // Initial-load bypass for the sphere cap. Starts true so the first
    // UpdateLoadedChunks call (driven from Initialize before the player
    // spawns) can load all ~900 sphere chunks synchronously — game spawn
    // already waits via IsSpawnChunkReady, so the synchronous hitch is
    // hidden under the loading screen. Flips false once the player's
    // current chunk is loaded; from then on sphere loads obey the cap so
    // a player crossing a chunk boundary mid-game doesn't trigger a
    // multi-hundred-ms surge.
    private bool _initialLoadPending = true;
    private Vector3I _lastPlayerChunkCoord;
    private float _lightFlushTimer;
    // Cached "desired chunk set" gate (see ShouldRebuildDesired). The desired
    // set is an O(MAX_LOAD_DISTANCE³) frustum scan that only changes when the
    // player crosses a chunk boundary or the camera turns, so we snapshot those
    // inputs and skip the scan on the (many) frames where neither moved.
    private bool _desiredValid;
    private Vector3I _desiredBuiltChunk;
    private Vector3 _desiredBuiltCamForward;
    private bool _desiredBuiltOverlook;
    // Camera-forward dot below which we treat the view as having turned enough
    // to need a fresh frustum scan (~1.8°).
    private const float DESIRED_CAM_ROTATION_DOT = 0.9995f;
    private Func<Vector3> _getPlayerPosition;
    private WorldState _worldData;
    private LightMap _lightMap;
    private SkyExposureMap _skyExposureMap;
    private FogMap _fogMap;
    private WindMap _windMap;
    private GpuParticlesAttractorVectorField3D _windAttractor;
    private WaterCurrentMap _waterCurrentMap;
    private ShaderMaterial _fogMaterial;
    private Camera3D _camera;

    public void Initialize(WorldState worldData, Vector3 spawnPosition, Camera3D camera, ShaderMaterial fogMaterial, Func<Vector3> getPlayerPosition)
    {
        _worldData = worldData;
        _lastPlayerChunkCoord = World.WorldToChunkCoord(spawnPosition);
        _lightMap = new LightMap(worldData, _lastPlayerChunkCoord, LIGHT_WINDOW_DIAMETER_CHUNKS);
        _skyExposureMap = new SkyExposureMap(worldData, _lastPlayerChunkCoord, LIGHT_WINDOW_DIAMETER_CHUNKS);
        _fogMap = new FogMap(worldData, _lastPlayerChunkCoord, LIGHT_WINDOW_DIAMETER_CHUNKS);
        _windMap = new WindMap(worldData, _lastPlayerChunkCoord, LIGHT_WINDOW_DIAMETER_CHUNKS);
        _waterCurrentMap = new WaterCurrentMap(worldData, _lastPlayerChunkCoord, LIGHT_WINDOW_DIAMETER_CHUNKS);
        _fogMaterial = fogMaterial;
        _camera = camera;
        _getPlayerPosition = getPlayerPosition;

        // IMPORTANT: register all global uniforms BEFORE touching any shader
        // material, because setting a parameter on a ShaderMaterial compiles
        // the shader if it hasn't compiled yet, and compilation fails if any
        // referenced global uniform is not yet registered. fog_volumetric,
        // voxel_clip, sprite_lit, voxel_water and water_clip_cap all read
        // `light_map`, so registering it after the fog material setup was
        // what caused "Global uniform 'light_map' does not exist" errors
        // on game start.
        //
        // light_map is declared in project.godot with a PlaceholderTexture3D
        // so the editor can compile these shaders when they're opened in the
        // script editor. At runtime we swap in the real ImageTexture3D here.
        ShaderGlobals.Register("light_map", RenderingServer.GlobalShaderParameterType.Sampler3D, _lightMap.Texture);
        ShaderGlobals.Register("light_map_origin", RenderingServer.GlobalShaderParameterType.Vec3, _lightMap.Origin);
        ShaderGlobals.Register("light_map_inv_size", RenderingServer.GlobalShaderParameterType.Vec3, _lightMap.InvSize);
        ShaderGlobals.Register("light_falloff_exp", RenderingServer.GlobalShaderParameterType.Float, 2f);
        // Night-vision degree for the screenspace effect in
        // shaders/post_process.gdshader. Seeded to 0 (off, exact no-op);
        // Player pushes the live degree each frame from its NightVision stat.
        ShaderGlobals.Register("night_vision", RenderingServer.GlobalShaderParameterType.Float, 0f);
        // SkyExposureMap — same origin/inv_size UVW convention as light_map.
        // Declared in project.godot with a PlaceholderTexture3D so the editor
        // can compile the rain shader that reads it; the real ImageTexture3D
        // is swapped in here at runtime.
        ShaderGlobals.Register("sky_exposure_map", RenderingServer.GlobalShaderParameterType.Sampler3D, _skyExposureMap.Texture);
        ShaderGlobals.Register("sky_exposure_map_origin", RenderingServer.GlobalShaderParameterType.Vec3, _skyExposureMap.Origin);
        ShaderGlobals.Register("sky_exposure_map_inv_size", RenderingServer.GlobalShaderParameterType.Vec3, _skyExposureMap.InvSize);
        // tree_lit.gdshader per-feature gates. Declared in project.godot's
        // [shader_globals] so the editor's script editor can compile the
        // shader on its own; seeded here with the current CVar value so a
        // cvars.txt override at startup is honored before the first tree
        // material compiles. Subsequent live edits push via the OnChanged
        // callbacks in CVars.cs.
        ShaderGlobals.Register("tree_wind_strength", RenderingServer.GlobalShaderParameterType.Float, CVars.treeWind.Value);
        ShaderGlobals.Register("tree_sphere_normal_strength", RenderingServer.GlobalShaderParameterType.Float, CVars.treeSphereNormal.Value);
        ShaderGlobals.Register("tree_detail_noise_strength", RenderingServer.GlobalShaderParameterType.Float, CVars.treeDetailNoise.Value);
        ShaderGlobals.Register("tree_silhouette_breakup_strength", RenderingServer.GlobalShaderParameterType.Float, CVars.treeSilhouetteBreakup.Value);
        // Contact/directional AO darkening strength for 3D model props (trunks,
        // rocks, chests, statues) — consumed by model_lit.gdshader. Global so a
        // single CVar drives every model_lit material at once.
        ShaderGlobals.Register("model_ao_strength", RenderingServer.GlobalShaderParameterType.Float, CVars.modelAo.Value);
        // Ceiling-cap pipeline debug mode (CVars.clipDebug). Seeded here so
        // every shader that reads it (clip_cap, water_clip_cap, voxel_water,
        // voxel_water_backface) compiles cleanly before the first frame.
        ShaderGlobals.Register("clip_debug_mode", RenderingServer.GlobalShaderParameterType.Int, CVars.clipDebug.Value);
        ShaderGlobals.Register("water_hide", RenderingServer.GlobalShaderParameterType.Bool, CVars.waterHide.Value);
        // block_light_shadow_* globals (the projector's coverage texture,
        // its world→UV matrix, and the on/off flag) are seeded by
        // BlockLightShadowProjector._Ready, which also runs before the
        // first chunk shader compiles. See that script for details.
        // Wind subgrid texture — same origin/inv_size convention as light_map
        // so a shader's `(world_pos - origin) * inv_size` UVW expression works
        // identically for either map. Declared in project.godot with a
        // PlaceholderTexture3D so the editor can compile shaders that read it.
        ShaderGlobals.Register("wind_map", RenderingServer.GlobalShaderParameterType.Sampler3D, _windMap.Texture);
        ShaderGlobals.Register("wind_map_origin", RenderingServer.GlobalShaderParameterType.Vec3, _windMap.Origin);
        ShaderGlobals.Register("wind_map_inv_size", RenderingServer.GlobalShaderParameterType.Vec3, _windMap.InvSize);
        // Maps stored RGB (signed [-1, 1]) to world m/s when shaders decode
        // wind_map velocity. Must match WindGen.WIND_VELOCITY_SCALE — change
        // them together (or re-bake chunks) so disk values keep their
        // intended magnitudes.
        ShaderGlobals.Register("wind_velocity_scale", RenderingServer.GlobalShaderParameterType.Float, WindGen.WIND_VELOCITY_SCALE);

        // Global wind force on GPU particles. The wind_map's RGB channel is
        // already encoded with byte 128 = signed zero, which is exactly what
        // GpuParticlesAttractorVectorField3D's vector_field texture expects
        // (0.5 = no force). Bounding box spans the full world extent so every
        // GPU particle in the scene sits inside it and reads its local wind
        // sample. Per-particle response falls out of each ParticleProcessMaterial's
        // existing `damping`: low-damping particles (embers, dust) drift far,
        // high-damping particles (blood, debris) barely budge — physically
        // intuitive without any per-effect authoring. Effects that should NOT
        // get wind (rain's falling drops, which already do their own wind tilt)
        // set attractor_interaction_enabled = false on their process material.
        _windAttractor = new GpuParticlesAttractorVectorField3D();
        _windAttractor.Name = "WindAttractor";
        _windAttractor.Texture = _windMap.Texture;
        // GpuParticlesAttractor3D.size is half-extents (the AABB is [-size, +size]
        // around the node position). The wind_map is now a toroidal window, so
        // the attractor's AABB — which maps linearly to the texture [0,1] with NO
        // wrap — must cover exactly one window tile aligned to the texel grid so
        // its uv matches the toroidal texel layout. RepositionWindAttractor snaps
        // it to the tile containing the player.
        _windAttractor.Size = _windMap.WindowWorldSize * 0.5f;
        RepositionWindAttractor();
        // Strength is the m/s² acceleration applied at peak signed wind (RGB
        // = 0 or 255). Defaults small because particles have low damping;
        // tunable live via CVars.particleWindStrength (polled in _Process).
        _windAttractor.Strength = WindGen.WIND_VELOCITY_SCALE * CVars.particleWindStrength.Value;
        AddChild(_windAttractor);
        // Water-current subgrid — same UVW convention as wind_map / light_map.
        // Declared in project.godot with a PlaceholderTexture3D so the editor
        // can compile voxel_water.gdshader; the runtime ImageTexture3D is
        // swapped in here before the water material first compiles.
        ShaderGlobals.Register("water_current_map", RenderingServer.GlobalShaderParameterType.Sampler3D, _waterCurrentMap.Texture);
        ShaderGlobals.Register("water_current_map_origin", RenderingServer.GlobalShaderParameterType.Vec3, _waterCurrentMap.Origin);
        ShaderGlobals.Register("water_current_map_inv_size", RenderingServer.GlobalShaderParameterType.Vec3, _waterCurrentMap.InvSize);
        ShaderGlobals.Register("water_current_speed", RenderingServer.GlobalShaderParameterType.Float, CVars.waterCurrentSpeed.Value);
        ShaderGlobals.Register("water_current_phase_period", RenderingServer.GlobalShaderParameterType.Float, CVars.waterCurrentPhasePeriod.Value);
        ShaderGlobals.Register("water_currents_enabled", RenderingServer.GlobalShaderParameterType.Bool, CVars.waterCurrentsEnabled.Value);
        // Day/night-driven sun controls. Intensity is overall brightness;
        // color is the RGB tint (warm at dawn/dusk, cool at noon, etc.).
        // Both default to "noon" values; the day/night sim will write them.
        // Bootstrap value — SkyController.Apply overwrites this every frame
        // with the day/night-blended CurrentPrimaryIntensity.
        ShaderGlobals.Register("sun_intensity", RenderingServer.GlobalShaderParameterType.Float, 2f);
        ShaderGlobals.Register("sun_color", RenderingServer.GlobalShaderParameterType.Vec3, CVars.SunColor);

        // Bird's-eye overlook fog reveal (fog_volumetric.gdshader). The curtain
        // hides any ground beyond overlook_reveal_radius (XZ distance from
        // overlook_reveal_center) behind full haze so chunks still streaming in
        // under the overview are masked; GameClient rides the radius out along
        // the load frontier (OverlookLoadedRadiusWorld). Declared in
        // project.godot so the fog material previews in-editor; default radius
        // huge = no curtain in normal play.
        ShaderGlobals.Register("overlook_reveal_center", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
        ShaderGlobals.Register("overlook_reveal_radius", RenderingServer.GlobalShaderParameterType.Float, 1e20f);
        ShaderGlobals.Register("overlook_reveal_softness", RenderingServer.GlobalShaderParameterType.Float, 24f);

        // Detail-sprite player-push globals (player_pos / player_radius /
        // player_strength) and wind globals live in project.godot's
        // [shader_globals]. They exist from editor startup, so the
        // detail_sprite shader compiles in editor preview and GameClient can
        // write per-frame updates without registering anything here.

        // Fog is rendered by a screen-space raymarching shader (see
        // shaders/fog_volumetric.gdshader), not by Godot's built-in FogVolume
        // — that pipeline requires a perspective camera, which breaks our
        // pixel-snapping art style. Fog uniforms are per-material (not global)
        // because this shader is the only consumer, and per-material uniforms
        // compile cleanly in the editor.
        if (_fogMaterial != null)
        {
            _fogMaterial.SetShaderParameter("fog_map", _fogMap.Texture);
            _fogMaterial.SetShaderParameter("fog_map_origin", _fogMap.Origin);
            _fogMaterial.SetShaderParameter("fog_map_inv_size", _fogMap.InvSize);
            _fogMaterial.SetShaderParameter("debug_mode", CVars.fogDebug.Value);
            _fogMaterial.SetShaderParameter("fog_enabled", CVars.fogEnabled.Value);
            _fogMaterial.SetShaderParameter("water_level", (float)WorldGen.WATER_LEVEL);
        }

        UpdateLoadedChunks();
    }

    public bool IsChunkLoaded(Vector3I coord)
    {
        return _loadedChunks.ContainsKey(coord);
    }

    public bool IsSpawnChunkReady(Vector3 spawnPosition)
    {
        Vector3I coord = World.WorldToChunkCoord(spawnPosition);
        return _loadedChunks.TryGetValue(coord, out ChunkMesh chunk) && chunk.CollisionReady;
    }

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("ChunkManager.Process");

        using (Profiler.Sample("ChunkManager.RebuildQueue"))
        {
            ProcessMeshRebuildQueue();
        }

        Vector3I prevPlayerChunk = _lastPlayerChunkCoord;
        _lastPlayerChunkCoord = World.WorldToChunkCoord(_getPlayerPosition());
        if (_lastPlayerChunkCoord != prevPlayerChunk)
        {
            // Slide the toroidal volume-map windows to follow the player. Only
            // the freshly-entered chunks are marked dirty (re-encoded in the
            // Flush calls below); the rest of the window keeps its texels.
            using (Profiler.Sample("ChunkManager.VolumeMapRecenter"))
            {
                _lightMap.Recenter(_lastPlayerChunkCoord);
                _skyExposureMap.Recenter(_lastPlayerChunkCoord);
                _fogMap.Recenter(_lastPlayerChunkCoord);
                _windMap.Recenter(_lastPlayerChunkCoord);
                _waterCurrentMap.Recenter(_lastPlayerChunkCoord);
                RepositionWindAttractor();
            }
        }
        using (Profiler.Sample("ChunkManager.UpdateLoadedChunks"))
        {
            UpdateLoadedChunks();
        }

        // Drain any direct WorldState writes (e.g. MovingLight per-frame
        // deposits) into LightMap, then flush. Throttled to light_flush_hz: the
        // flush does a FULL-texture GPU upload (ImageTexture3D has no partial
        // update), so a light that dirties a chunk every frame (flicker, a moving
        // torch) would otherwise force a full re-upload per frame. Dirty chunks
        // accumulate (deduped) in WorldState between flushes and batch into one
        // upload here. Geometry/light-add paths still drain immediately; only the
        // upload is rate-capped.
        _lightFlushTimer += (float)delta;
        if (_lightFlushTimer >= 1f / Mathf.Max(1f, CVars.lightFlushHz.Value))
        {
            _lightFlushTimer = 0f;
            using (Profiler.Sample("ChunkManager.LightFlush"))
            {
                DrainLightChunkDirty();
                _lightMap.Flush(_worldData);
            }
        }

        using (Profiler.Sample("ChunkManager.SkyExposureFlush"))
        {
            DrainSkyExposureChunkDirty();
            _skyExposureMap.Flush(_worldData);
        }

        using (Profiler.Sample("ChunkManager.FogFlush"))
        {
            DrainFogChunkDirty();
            _fogMap.Flush(_worldData);
        }

        using (Profiler.Sample("ChunkManager.WaterCurrentFlush"))
        {
            DrainWaterCurrentChunkDirty();
            _waterCurrentMap.Flush(_worldData);
        }

        using (Profiler.Sample("ChunkManager.WindFlush"))
        {
            DrainWindChunkDirty();
            _windMap.Flush(_worldData);
        }

        if (_windAttractor != null)
        {
            // Live re-poll so tuning particle_wind_strength via the in-game
            // console takes effect immediately. Cheap: just a float assignment.
            _windAttractor.Strength = WindGen.WIND_VELOCITY_SCALE * CVars.particleWindStrength.Value;
        }
    }

    public void UpdateLighting(List<Vector3I> changedPositions)
    {
        _worldData.OnVoxelsChanged(changedPositions);
        DrainLightChunkDirty();
    }

    public void AddLightSource(LightSource source)
    {
        _worldData.AddLightSource(source);
        DrainLightChunkDirty();
    }

    public void RemoveLightSource(LightSource source)
    {
        _worldData.RemoveLightSource(source);
        DrainLightChunkDirty();
    }

    public void SetLightAmplitude(LightSource source, float amplitude)
    {
        _worldData.SetLightAmplitude(source, amplitude);
        DrainLightChunkDirty();
    }

    public void SetFogDebugMode(int mode)
    {
        _fogMaterial?.SetShaderParameter("debug_mode", mode);
    }

    public void SetFogEnabled(bool enabled)
    {
        _fogMaterial?.SetShaderParameter("fog_enabled", enabled);
    }

    public void SetFogVolumetricEnabled(bool enabled)
    {
        _fogMaterial?.SetShaderParameter("fog_volumetric_enabled", enabled);
    }

    // Snaps the wind particle attractor's AABB to the toroidal wind_map tile
    // containing the player. The attractor maps its box linearly to the texture
    // [0,1] with no wrap, so the box must align to a window tile [k*W, (k+1)*W]
    // for its uv to match the wrapped texel layout. Crossing a tile boundary
    // jumps the box by one window (a one-frame wind discontinuity at the box
    // edge — far from the player, negligible).
    private void RepositionWindAttractor()
    {
        if (_windAttractor == null) { return; }
        Vector3 wws = _windMap.WindowWorldSize;
        Vector3 p = _getPlayerPosition();
        Vector3 tileMin = new Vector3(
            Mathf.Floor(p.X / wws.X) * wws.X,
            Mathf.Floor(p.Y / wws.Y) * wws.Y,
            Mathf.Floor(p.Z / wws.Z) * wws.Z);
        _windAttractor.Position = tileMin + wws * 0.5f;
    }

    // Moves dirty marks from WorldState into LightMap. The actual encode +
    // upload happens once per frame in _Process; this lets carrier lights
    // do remove+add per frame without paying for two uploads each time.
    private void DrainLightChunkDirty()
    {
        if (_worldData.LightChunkDirty.Count == 0) { return; }
        // Count distinct chunk light-texture re-uploads queued this frame. A high
        // sustained number while standing still is the signature of flicker churn
        // (every flickering light re-deposits → re-dirties its chunks each tick).
        Profiler.IncrementCounter("light_chunk_uploads", _worldData.LightChunkDirty.Count);
        foreach (Vector3I coord in _worldData.LightChunkDirty)
        {
            _lightMap.MarkChunkDirty(coord);
        }
        _worldData.LightChunkDirty.Clear();
    }

    private void DrainSkyExposureChunkDirty()
    {
        if (_worldData.SkyExposureChunkDirty.Count == 0) { return; }
        foreach (Vector3I coord in _worldData.SkyExposureChunkDirty)
        {
            _skyExposureMap.MarkChunkDirty(coord);
        }
        _worldData.SkyExposureChunkDirty.Clear();
    }

    private void DrainFogChunkDirty()
    {
        if (_worldData.FogChunkDirty.Count == 0) { return; }
        foreach (Vector3I coord in _worldData.FogChunkDirty)
        {
            _fogMap.MarkChunkDirty(coord);
        }
        _worldData.FogChunkDirty.Clear();
    }

    private void DrainWaterCurrentChunkDirty()
    {
        if (_worldData.WaterCurrentChunkDirty.Count == 0) { return; }
        foreach (Vector3I coord in _worldData.WaterCurrentChunkDirty)
        {
            _waterCurrentMap.MarkChunkDirty(coord);
        }
        _worldData.WaterCurrentChunkDirty.Clear();
    }

    private void DrainWindChunkDirty()
    {
        if (_worldData.WindChunkDirty.Count == 0) { return; }
        foreach (Vector3I coord in _worldData.WindChunkDirty)
        {
            _windMap.MarkChunkDirty(coord);
        }
        _worldData.WindChunkDirty.Clear();
    }

    public void RebuildNearbyChunkMeshes(Vector3 worldPos, List<Vector3I> changedPositions)
    {
        UpdateLighting(changedPositions);

        Vector3I center = World.WorldToChunkCoord(worldPos);
        foreach (Vector3I coord in _loadedChunks.Keys)
        {
            if (Math.Abs(coord.X - center.X) <= 1 && Math.Abs(coord.Y - center.Y) <= 1 && Math.Abs(coord.Z - center.Z) <= 1)
            {
                _meshRebuildQueue.Enqueue(coord);
            }
        }
    }

    // True world-direction the camera is looking (−Z of its basis), or a stable
    // default before the camera is in the tree.
    private Vector3 CameraForward()
    {
        if (_camera != null && _camera.IsInsideTree())
        {
            return (-_camera.GlobalTransform.Basis.Z).Normalized();
        }
        return Vector3.Forward;
    }

    // The desired-chunk set only changes when the player crosses a chunk
    // boundary (moving the always-loaded sphere) or the camera turns (moving
    // the frustum). Rebuilding it is an O(MAX_LOAD_DISTANCE³) frustum scan —
    // ~7 ms/frame even standing still — so skip it until one of those inputs
    // changes. Sub-chunk translation is deliberately ignored: any far chunk it
    // would newly pull into view is still many chunks away and gets picked up
    // at the next chunk crossing, and the NEARBY sphere always covers the
    // player's immediate surroundings.
    private bool ShouldRebuildDesired()
    {
        if (!_desiredValid) { return true; }
        // Overlook zooms/pans its own wide frustum (and can change its load
        // distance without a camera turn), so always re-scan while it's active
        // — that keeps the panorama-streaming behaviour byte-identical.
        if (_overlookActive || _desiredBuiltOverlook) { return true; }
        if (_lastPlayerChunkCoord != _desiredBuiltChunk) { return true; }
        return CameraForward().Dot(_desiredBuiltCamForward) < DESIRED_CAM_ROTATION_DOT;
    }

    // Recompute the desired-chunk set (always-loaded sphere + frustum-visible
    // chunks out to MAX_LOAD_DISTANCE) and snapshot the inputs it was built
    // from for ShouldRebuildDesired.
    private void RebuildDesiredSet(HashSet<Vector3I> desired)
    {
        desired.Clear();

        // Always load a sphere of chunks around the player for collision, gameplay,
        // and entity spawning. Spherical (not cubic) so the load boundary is at the
        // same world-space distance in every direction.
        for (int x = -NEARBY_RADIUS; x <= NEARBY_RADIUS; x++)
        {
            for (int y = -NEARBY_RADIUS; y <= NEARBY_RADIUS; y++)
            {
                for (int z = -NEARBY_RADIUS; z <= NEARBY_RADIUS; z++)
                {
                    if (x * x + y * y + z * z > NEARBY_RADIUS_SQ)
                    {
                        continue;
                    }
                    desired.Add(_lastPlayerChunkCoord + new Vector3I(x, y, z));
                }
            }
        }

        // Load frustum-visible chunks up to max distance
        if (_camera != null && _camera.IsInsideTree())
        {
            Godot.Collections.Array<Plane> frustumPlanes = _camera.GetFrustum();
            // Overlook extends the search to a wide, shallow cylinder (far in
            // XZ, thin in Y) and relies on the frustum test alone to cull;
            // normal play keeps the symmetric sphere so the load boundary is
            // the same world distance in every direction.
            int searchXZ = _overlookActive ? _overlookLoadDistance : MAX_LOAD_DISTANCE;
            int searchY = _overlookActive ? OVERLOOK_Y_BAND : MAX_LOAD_DISTANCE;
            for (int x = -searchXZ; x <= searchXZ; x++)
            {
                for (int y = -searchY; y <= searchY; y++)
                {
                    for (int z = -searchXZ; z <= searchXZ; z++)
                    {
                        if (!_overlookActive && x * x + y * y + z * z > MAX_LOAD_DISTANCE_SQ)
                        {
                            continue;
                        }

                        Vector3I coord = _lastPlayerChunkCoord + new Vector3I(x, y, z);

                        if (desired.Contains(coord))
                        {
                            continue;
                        }

                        Aabb chunkAabb = new Aabb(
                            new Vector3(
                                coord.X * ChunkState.SIZE,
                                coord.Y * ChunkState.SIZE,
                                coord.Z * ChunkState.SIZE),
                            new Vector3(ChunkState.SIZE, ChunkState.SIZE, ChunkState.SIZE)
                        );

                        if (IsAabbInFrustum(chunkAabb, frustumPlanes))
                        {
                            desired.Add(coord);
                        }
                    }
                }
            }
        }

        _desiredValid = true;
        _desiredBuiltChunk = _lastPlayerChunkCoord;
        _desiredBuiltOverlook = _overlookActive;
        _desiredBuiltCamForward = CameraForward();
    }

    private void UpdateLoadedChunks()
    {
        HashSet<Vector3I> desired = _desiredScratch;

        // Cached gate: only re-scan the frustum when the player crossed a chunk
        // or the camera turned. The unload/load passes below still run every
        // frame, so streaming keeps catching up to the (possibly cached) set.
        if (ShouldRebuildDesired())
        {
            RebuildDesiredSet(desired);
        }

        // Unload chunks no longer needed. The toRemove scan is cheap; the
        // QueueFree loop is what queues the deferred end-of-frame work
        // (Jolt body removal, GPU mesh release, child entity frees) that
        // the hitch detector catches as gap_ms. Capped at
        // MAX_UNLOADS_PER_FRAME to bound the deferred batch each frame —
        // chunks skipped this frame are picked up next frame because
        // _loadedChunks still contains them.
        List<Vector3I> toRemove = _toRemoveScratch;
        toRemove.Clear();
        foreach (Vector3I coord in _loadedChunks.Keys)
        {
            if (!desired.Contains(coord))
            {
                toRemove.Add(coord);
            }
        }
        using (Profiler.Sample("ChunkManager.UnloadChunks"))
        {
            int unloaded = 0;
            foreach (Vector3I coord in toRemove)
            {
                if (unloaded >= MAX_UNLOADS_PER_FRAME)
                {
                    break;
                }
                onChunkUnloaded?.Invoke(coord);
                ChunkMesh evicting = _loadedChunks[coord];
                if (CVars.chunkWaterLog.Value && evicting.HasWater)
                {
                    GD.Print($"[water_log] UNLOAD water chunk {coord} tod={_worldData.TimeOfDay01:F3} loaded={_loadedChunks.Count - 1}");
                }
                evicting.QueueFree();
                _loadedChunks.Remove(coord);
                unloaded++;
            }
        }

        // Pass 1: load near-neighbor (3x3x3 box around player) chunks
        // immediately, uncapped — these are where the player can step in one
        // frame and missing them means walking off the world's edge. Also
        // bucket the remaining unloaded chunks for distance-sorted loading
        // in the next pass.
        _loadCandidatesScratch.Clear();
        foreach (Vector3I coord in desired)
        {
            if (_loadedChunks.ContainsKey(coord))
            {
                continue;
            }
            Vector3I rel = coord - _lastPlayerChunkCoord;
            int distSq = rel.X * rel.X + rel.Y * rel.Y + rel.Z * rel.Z;
            if (distSq <= NEAR_LOAD_RADIUS_SQ)
            {
                LoadChunkInternal(coord);
            }
            else
            {
                _loadCandidatesScratch.Add(coord);
            }
        }

        // Pass 2: sort candidates by distance from player. Closest first so
        // the most-visible chunk fills before far ones the player can barely
        // see at the edge of MAX_LOAD_DISTANCE.
        _sortRefCoord = _lastPlayerChunkCoord;
        _loadCandidatesScratch.Sort(_byDistSqFromRef);

        // Pass 3: load up to per-frame caps in distance order. Sphere chunks
        // (within NEARBY_RADIUS) and frustum-extension chunks (beyond it)
        // cap independently. The initial-load bypass lets the first call —
        // driven from Initialize before the player spawns — fill the whole
        // sphere in one synchronous pass; spawn waits via IsSpawnChunkReady,
        // so that hitch is hidden under the loading screen.
        int sphereLoaded = 0;
        int frustumLoaded = 0;
        foreach (Vector3I coord in _loadCandidatesScratch)
        {
            Vector3I rel = coord - _lastPlayerChunkCoord;
            int distSq = rel.X * rel.X + rel.Y * rel.Y + rel.Z * rel.Z;
            bool isSphereChunk = distSq <= NEARBY_RADIUS_SQ;
            if (isSphereChunk)
            {
                if (!_initialLoadPending && sphereLoaded >= MAX_SPHERE_LOADS_PER_FRAME)
                {
                    continue;
                }
                sphereLoaded++;
            }
            else
            {
                int frustumCap = _overlookActive ? MAX_OVERLOOK_LOADS_PER_FRAME : MAX_FRUSTUM_LOADS_PER_FRAME;
                if (frustumLoaded >= frustumCap)
                {
                    continue;
                }
                frustumLoaded++;
            }
            LoadChunkInternal(coord);
        }

        // Track the overlook frontier (nearest desired-but-unloaded chunk) so
        // GameClient's fog reveal only uncovers ground that's actually resident.
        if (_overlookActive)
        {
            UpdateOverlookFrontier(desired);
        }

        // Flip the initial-load bypass off once the player's current chunk
        // exists. After Initialize's one-shot pass this is true on the same
        // call that loaded it; subsequent _Process calls obey the cap.
        if (_initialLoadPending && _loadedChunks.ContainsKey(_lastPlayerChunkCoord))
        {
            _initialLoadPending = false;
        }
    }

    // Nearest desired-but-unloaded chunk = the frontier; everything closer is
    // resident. Center-out loading (the Pass 2 distance sort) makes it grow
    // monotonically as the panorama fills. Reported as a world radius shrunk by
    // one chunk so the fog edge sits at the INNER face of the first unloaded
    // chunk — the reveal never uncovers a chunk that isn't there yet.
    private void UpdateOverlookFrontier(HashSet<Vector3I> desired)
    {
        int minUnloadedDistSq = int.MaxValue;
        foreach (Vector3I coord in desired)
        {
            if (_loadedChunks.ContainsKey(coord))
            {
                continue;
            }
            // A desired coord with no chunk data is out of world bounds (the
            // wide overlook frustum + Y band overshoot the edges) or genuinely
            // empty — there is nothing to stream there, so it must NOT count as
            // a pending frontier chunk. Without this, the nearest out-of-bounds
            // air chunk collapses the frontier to a few metres and the reveal
            // curtain blankets the whole overview in fog.
            if (_worldData.GetChunk(coord) == null)
            {
                continue;
            }
            Vector3I rel = coord - _lastPlayerChunkCoord;
            int distSq = rel.X * rel.X + rel.Y * rel.Y + rel.Z * rel.Z;
            if (distSq < minUnloadedDistSq)
            {
                minUnloadedDistSq = distSq;
            }
        }
        if (minUnloadedDistSq == int.MaxValue)
        {
            // Whole desired set resident — reveal the full panorama.
            _overlookFrontierRadiusWorld = _overlookLoadDistance * ChunkState.SIZE;
        }
        else
        {
            float chunkRadius = Mathf.Max(0f, Mathf.Sqrt(minUnloadedDistSq) - 1f);
            _overlookFrontierRadiusWorld = chunkRadius * ChunkState.SIZE;
        }
    }

    private void LoadChunkInternal(Vector3I coord)
    {
        ChunkState data = _worldData.GetChunk(coord);
        if (data == null)
        {
            return;
        }
        // Chunks beyond the normal load distance only ever exist as bird's-eye
        // overlook backdrop — skip collision AND detail scatter for them. Within
        // MAX_LOAD_DISTANCE this is always false, so normal play is unchanged.
        Vector3I rel = coord - _lastPlayerChunkCoord;
        bool visualOnly = (rel.X * rel.X + rel.Y * rel.Y + rel.Z * rel.Z) > MAX_LOAD_DISTANCE_SQ;
        ChunkMesh mesh = ChunkMesh.Create(data, _worldData.GetVoxelWorld, _worldData.GetShapeWorld, _worldData.GetTerrainIdWorld, _worldData.GetOverlayIdWorld, _worldData.IsInBounds, buildCollision: !visualOnly, buildDetails: !visualOnly);
        AddChild(mesh);
        _loadedChunks[coord] = mesh;
        if (CVars.chunkWaterLog.Value && mesh.HasWater)
        {
            GD.Print($"[water_log] LOAD   water chunk {coord} tod={_worldData.TimeOfDay01:F3} loaded={_loadedChunks.Count}");
        }
        onChunkLoaded?.Invoke(coord);
    }

    private void ProcessMeshRebuildQueue()
    {
        int rebuilt = 0;
        while (_meshRebuildQueue.Count > 0 && rebuilt < MAX_REBUILDS_PER_FRAME)
        {
            Vector3I coord = _meshRebuildQueue.Dequeue();
            if (!_loadedChunks.TryGetValue(coord, out ChunkMesh oldMesh))
            {
                continue;
            }

            ChunkState data = _worldData.GetChunk(coord);
            if (data == null)
            {
                continue;
            }

            oldMesh.QueueFree();
            ChunkMesh mesh = ChunkMesh.Create(data, _worldData.GetVoxelWorld, _worldData.GetShapeWorld, _worldData.GetTerrainIdWorld, _worldData.GetOverlayIdWorld, _worldData.IsInBounds);
            AddChild(mesh);
            _loadedChunks[coord] = mesh;
            rebuilt++;
        }
    }

    private static bool IsAabbInFrustum(Aabb aabb, Godot.Collections.Array<Plane> planes)
    {
        foreach (Plane plane in planes)
        {
            Vector3 nearVertex = new Vector3(
                plane.Normal.X > 0 ? aabb.Position.X : aabb.End.X,
                plane.Normal.Y > 0 ? aabb.Position.Y : aabb.End.Y,
                plane.Normal.Z > 0 ? aabb.Position.Z : aabb.End.Z
            );

            if (plane.DistanceTo(nearVertex) > 0)
            {
                return false;
            }
        }
        return true;
    }
}
