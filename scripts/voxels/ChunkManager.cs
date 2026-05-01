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
    private const int MAX_LOAD_DISTANCE = 10;
    private const int MAX_REBUILDS_PER_FRAME = 3;

    public event Action<Vector3I> onChunkLoaded;
    public event Action<Vector3I> onChunkUnloaded;

    private readonly Dictionary<Vector3I, ChunkMesh> _loadedChunks = new();
    private readonly Queue<Vector3I> _meshRebuildQueue = new();
    private Vector3I _lastPlayerChunkCoord;
    private Func<Vector3> _getPlayerPosition;
    private WorldState _worldData;
    private LightMap _lightMap;
    private FogMap _fogMap;
    private WindMap _windMap;
    private ShaderMaterial _fogMaterial;
    private Camera3D _camera;

    public void Initialize(WorldState worldData, Vector3 spawnPosition, Camera3D camera, ShaderMaterial fogMaterial, Func<Vector3> getPlayerPosition)
    {
        _worldData = worldData;
        _lightMap = new LightMap(worldData);
        _fogMap = new FogMap(worldData);
        _windMap = new WindMap(worldData);
        _fogMaterial = fogMaterial;
        _camera = camera;
        _getPlayerPosition = getPlayerPosition;
        _lastPlayerChunkCoord = World.WorldToChunkCoord(spawnPosition);

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
        ShaderGlobals.Register("light_map_inv_size", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.One / _lightMap.Size);
        ShaderGlobals.Register("light_falloff_exp", RenderingServer.GlobalShaderParameterType.Float, 2f);
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
        ShaderGlobals.Register("wind_map_inv_size", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.One / _windMap.Size);
        // Day/night-driven sun controls. Intensity is overall brightness;
        // color is the RGB tint (warm at dawn/dusk, cool at noon, etc.).
        // Both default to "noon" values; the day/night sim will write them.
        ShaderGlobals.Register("sun_intensity", RenderingServer.GlobalShaderParameterType.Float, CVars.sunIntensity.Value);
        ShaderGlobals.Register("sun_color", RenderingServer.GlobalShaderParameterType.Vec3, CVars.SunColor);

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
            _fogMaterial.SetShaderParameter("fog_map_inv_size", Vector3.One / _fogMap.Size);
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
        ProcessMeshRebuildQueue();

        _lastPlayerChunkCoord = World.WorldToChunkCoord(_getPlayerPosition());
        UpdateLoadedChunks();

        // Drain any direct WorldState writes (e.g. CarrierLight per-frame
        // deposits) into LightMap, then flush. This is the single per-frame
        // upload point — all light changes within this frame batch here.
        DrainLightChunkDirty();
        _lightMap.Flush(_worldData, _loadedChunks.Keys);

        DrainFogChunkDirty();
        _fogMap.Flush(_worldData, _loadedChunks.Keys);
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

    // Moves dirty marks from WorldState into LightMap. The actual encode +
    // upload happens once per frame in _Process; this lets carrier lights
    // do remove+add per frame without paying for two uploads each time.
    private void DrainLightChunkDirty()
    {
        if (_worldData.LightChunkDirty.Count == 0) { return; }
        foreach (Vector3I coord in _worldData.LightChunkDirty)
        {
            _lightMap.MarkChunkDirty(coord);
        }
        _worldData.LightChunkDirty.Clear();
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

    private void UpdateLoadedChunks()
    {
        var desired = new HashSet<Vector3I>();

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
            int maxDistSq = MAX_LOAD_DISTANCE * MAX_LOAD_DISTANCE;
            for (int x = -MAX_LOAD_DISTANCE; x <= MAX_LOAD_DISTANCE; x++)
            {
                for (int y = -MAX_LOAD_DISTANCE; y <= MAX_LOAD_DISTANCE; y++)
                {
                    for (int z = -MAX_LOAD_DISTANCE; z <= MAX_LOAD_DISTANCE; z++)
                    {
                        if (x * x + y * y + z * z > maxDistSq)
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

        // Unload chunks no longer needed
        var toRemove = new List<Vector3I>();
        foreach (Vector3I coord in _loadedChunks.Keys)
        {
            if (!desired.Contains(coord))
            {
                toRemove.Add(coord);
            }
        }
        foreach (Vector3I coord in toRemove)
        {
            onChunkUnloaded?.Invoke(coord);
            _loadedChunks[coord].QueueFree();
            _loadedChunks.Remove(coord);
        }

        // Load new chunks from world data
        foreach (Vector3I coord in desired)
        {
            if (!_loadedChunks.ContainsKey(coord))
            {
                ChunkState data = _worldData.GetChunk(coord);
                if (data == null)
                {
                    continue;
                }
                ChunkMesh mesh = ChunkMesh.Create(data, _worldData.GetVoxelWorld, _worldData.GetShapeWorld, _worldData.GetKitIdWorld, _worldData.GetOverlayIdWorld, _worldData.IsInBounds);
                AddChild(mesh);
                _loadedChunks[coord] = mesh;
                onChunkLoaded?.Invoke(coord);
            }
        }
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
            ChunkMesh mesh = ChunkMesh.Create(data, _worldData.GetVoxelWorld, _worldData.GetShapeWorld, _worldData.GetKitIdWorld, _worldData.GetOverlayIdWorld, _worldData.IsInBounds);
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
