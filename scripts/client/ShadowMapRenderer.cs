using System.Collections.Generic;
using Godot;

// Renders entity silhouettes from the sun's perspective into a SubViewport,
// then exposes that texture as a 2D shadow map. Receivers (voxel_clip,
// sprite_lit) sample it via the global shadow_origin / shadow_axis_u /
// shadow_axis_v / shadow_depth_axis uniforms using a world-space dot-product
// projection, with a light-axis depth test so casters only shadow geometry
// behind them along the light ray.
[GlobalClass]
public partial class ShadowMapRenderer : Node3D
{
    private const float ORTHO_SIZE = 48f;
    private const int VIEWPORT_SIZE = 1024;
    private const float BACK_DISTANCE = 80f;
    private const float CAMERA_NEAR = 0.1f;
    private const float CAMERA_FAR = 200f;

    private SubViewport _viewport;
    private Camera3D _camera;
    private Node3D _proxyRoot;
    private Shader _casterShader;
    private ShaderMaterial _meshCasterMaterial;

    private readonly Dictionary<Sprite3D, Sprite3D> _proxies = new();
    private readonly Dictionary<MeshInstance3D, MeshInstance3D> _meshProxies = new();
    private readonly List<Sprite3D> _frameSources = new();
    private readonly List<MeshInstance3D> _frameMeshSources = new();
    private readonly HashSet<Sprite3D> _seenSprites = new();
    private readonly HashSet<MeshInstance3D> _seenMeshes = new();
    private readonly List<Sprite3D> _spriteRemove = new();
    private readonly List<MeshInstance3D> _meshRemove = new();

    public Texture2D ShadowMap => _viewport?.GetTexture();

    public override void _Ready()
    {
        _casterShader = GD.Load<Shader>("res://shaders/shadow_caster.gdshader");
        var meshCasterShader = GD.Load<Shader>("res://shaders/shadow_caster_mesh.gdshader");
        _meshCasterMaterial = new ShaderMaterial();
        _meshCasterMaterial.Shader = meshCasterShader;

        RegisterGlobalParam("shadow_origin", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
        RegisterGlobalParam("shadow_axis_u", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
        RegisterGlobalParam("shadow_axis_v", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
        RegisterGlobalParam("shadow_depth_axis", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
        RegisterGlobalParam("shadow_strength", RenderingServer.GlobalShaderParameterType.Float, 0f);
        RegisterGlobalParam("shadow_color", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
        RegisterGlobalParam("sprite_stretch", RenderingServer.GlobalShaderParameterType.Float, 1f);

        _viewport = new SubViewport();
        _viewport.Size = new Vector2I(VIEWPORT_SIZE, VIEWPORT_SIZE);
        _viewport.OwnWorld3D = true;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _viewport.Msaa3D = Viewport.Msaa.Disabled;
        _viewport.HandleInputLocally = false;
        _viewport.GuiDisableInput = true;
        // Half-float render target so small depth deltas (~0.002-0.008 in
        // normalized light-axis depth) survive the round-trip through the
        // texture instead of being crushed by 8-bit sRGB quantization.
        _viewport.UseHdr2D = true;
        AddChild(_viewport);

        var env = new Environment();
        env.BackgroundMode = Environment.BGMode.Color;
        env.BackgroundColor = Colors.White;
        env.AmbientLightSource = Environment.AmbientSource.Color;
        env.AmbientLightColor = Colors.White;
        env.AmbientLightEnergy = 1f;
        env.TonemapMode = Environment.ToneMapper.Linear;
        var worldEnv = new WorldEnvironment();
        worldEnv.Environment = env;
        _viewport.AddChild(worldEnv);

        _camera = new Camera3D();
        _camera.Projection = Camera3D.ProjectionType.Orthogonal;
        _camera.Size = ORTHO_SIZE;
        _camera.Near = CAMERA_NEAR;
        _camera.Far = CAMERA_FAR;
        _camera.Current = true;
        _viewport.AddChild(_camera);

        _proxyRoot = new Node3D();
        _proxyRoot.Name = "Proxies";
        _viewport.AddChild(_proxyRoot);
    }

    public override void _Process(double delta)
    {
        if (World.Current == null)
        {
            return;
        }

        UpdateCamera();
        SyncProxies();
        PushGlobalUniforms(_camera.GlobalPosition, _camera.GlobalBasis);
    }

    private void UpdateCamera()
    {
        Vector3 lightDir = World.Current.WorldState.ShadowLightDirection;
        Vector3 target = World.Current.player?.GlobalPosition ?? Vector3.Zero;
        Vector3 camPos = target - lightDir * BACK_DISTANCE;
        _camera.GlobalPosition = camPos;
        _camera.LookAt(target, Vector3.Up);

        // Texel-snap the camera position in its own light-plane basis so the
        // shadow texture doesn't swim beneath static terrain as the player
        // moves.
        Basis basis = _camera.GlobalBasis;
        Vector3 right = basis.X;
        Vector3 up = basis.Y;
        Vector3 forward = basis.Z;

        float texel = ORTHO_SIZE / VIEWPORT_SIZE;
        float u = right.Dot(camPos);
        float v = up.Dot(camPos);
        float w = forward.Dot(camPos);

        float us = Mathf.Floor(u / texel) * texel;
        float vs = Mathf.Floor(v / texel) * texel;

        _camera.GlobalPosition = right * us + up * vs + forward * w;
    }

    private void PushGlobalUniforms(Vector3 camPos, Basis basis)
    {
        // basis.X / basis.Y are unit-length; scaling by 1/ORTHO_SIZE makes
        // dot(rel, axis) fall in [-0.5, 0.5] across the frustum, which the
        // receiver shaders then offset by +0.5 to get [0, 1] UVs.
        Vector3 axisU = basis.X / ORTHO_SIZE;
        Vector3 axisV = basis.Y / ORTHO_SIZE;
        // Depth axis: camera forward is -basis.Z. Scaling by 1/Far maps
        // world depth along the light direction into [0, 1], matching the
        // caster shader's output and the viewport's white (1.0 = far) clear.
        Vector3 depthAxis = -basis.Z / CAMERA_FAR;

        RenderingServer.GlobalShaderParameterSet("shadow_origin", camPos);
        RenderingServer.GlobalShaderParameterSet("shadow_axis_u", axisU);
        RenderingServer.GlobalShaderParameterSet("shadow_axis_v", axisV);
        RenderingServer.GlobalShaderParameterSet("shadow_depth_axis", depthAxis);
        WorldState ws = World.Current.WorldState;
        float strength = ws.ShadowStrength * CVars.shadowStrengthMultiplier.Value;
        RenderingServer.GlobalShaderParameterSet("shadow_strength", strength);
        RenderingServer.GlobalShaderParameterSet("shadow_color", new Vector3(ws.ShadowColor.R, ws.ShadowColor.G, ws.ShadowColor.B));
    }

    private void SyncProxies()
    {
        _frameSources.Clear();
        _frameMeshSources.Clear();
        CollectSources();

        _seenSprites.Clear();
        foreach (Sprite3D source in _frameSources)
        {
            _seenSprites.Add(source);
            if (!_proxies.TryGetValue(source, out Sprite3D proxy))
            {
                proxy = CreateProxy();
                _proxies[source] = proxy;
                _proxyRoot.AddChild(proxy);
            }
            SyncProxy(source, proxy);
        }
        PruneProxies(_proxies, _seenSprites, _spriteRemove);

        _seenMeshes.Clear();
        foreach (MeshInstance3D source in _frameMeshSources)
        {
            _seenMeshes.Add(source);
            if (!_meshProxies.TryGetValue(source, out MeshInstance3D proxy))
            {
                proxy = CreateMeshProxy();
                _meshProxies[source] = proxy;
                _proxyRoot.AddChild(proxy);
            }
            SyncMeshProxy(source, proxy);
        }
        PruneProxies(_meshProxies, _seenMeshes, _meshRemove);
    }

    private static void PruneProxies<T>(Dictionary<T, T> proxies, HashSet<T> seen, List<T> scratch) where T : Node
    {
        scratch.Clear();
        foreach (T source in proxies.Keys)
        {
            if (!seen.Contains(source) || !GodotObject.IsInstanceValid(source))
            {
                scratch.Add(source);
            }
        }
        foreach (T source in scratch)
        {
            T proxy = proxies[source];
            proxies.Remove(source);
            if (GodotObject.IsInstanceValid(proxy))
            {
                proxy.QueueFree();
            }
        }
    }

    private void CollectSources()
    {
        World world = World.Current;

        if (world.player != null)
        {
            CollectCasters(world.player);
        }

        foreach (List<Node3D> entities in world.ActiveEntities.Values)
        {
            foreach (Node3D entity in entities)
            {
                if (!GodotObject.IsInstanceValid(entity))
                {
                    continue;
                }
                CollectCasters(entity);
            }
        }
    }

    private void CollectCasters(Node node)
    {
        // Entity-level opt-out (e.g. Mob discovery state). Intentionally
        // separate from scene-tree Visible, which the ceiling clip toggles
        // — those shadows must still cast; the receiver discards pixels
        // above the clip plane.
        if (node is IShadowFilter filter && !filter.CastsShadow)
        {
            return;
        }
        if (node is Sprite3D sprite && sprite.Texture != null)
        {
            _frameSources.Add(sprite);
        }
        else if (node is MeshInstance3D mesh && mesh.Mesh != null)
        {
            _frameMeshSources.Add(mesh);
        }
        foreach (Node child in node.GetChildren())
        {
            CollectCasters(child);
        }
    }

    private Sprite3D CreateProxy()
    {
        var proxy = new Sprite3D();
        var mat = new ShaderMaterial();
        mat.Shader = _casterShader;
        proxy.MaterialOverride = mat;
        proxy.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        proxy.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
        proxy.AlphaCut = SpriteBase3D.AlphaCutMode.Disabled;
        return proxy;
    }

    private static void SyncProxy(Sprite3D source, Sprite3D proxy)
    {
        proxy.GlobalPosition = source.GlobalPosition;
        proxy.Texture = source.Texture;
        proxy.Centered = source.Centered;
        proxy.Offset = source.Offset;
        proxy.PixelSize = source.PixelSize;
        proxy.RegionEnabled = source.RegionEnabled;
        proxy.RegionRect = source.RegionRect;
        proxy.FlipH = source.FlipH;
        proxy.FlipV = source.FlipV;

        if (proxy.MaterialOverride is ShaderMaterial mat)
        {
            Vector2I spriteSize;
            Vector2I regionOrigin;
            if (source.RegionEnabled)
            {
                Rect2 r = source.RegionRect;
                spriteSize = new Vector2I((int)r.Size.X, (int)r.Size.Y);
                regionOrigin = new Vector2I((int)r.Position.X, (int)r.Position.Y);
            }
            else
            {
                spriteSize = new Vector2I(source.Texture.GetWidth(), source.Texture.GetHeight());
                regionOrigin = Vector2I.Zero;
            }
            mat.SetShaderParameter("sprite_texture", source.Texture);
            mat.SetShaderParameter("sprite_size", spriteSize);
            mat.SetShaderParameter("sprite_region_origin", regionOrigin);
        }
    }

    private MeshInstance3D CreateMeshProxy()
    {
        var proxy = new MeshInstance3D();
        proxy.MaterialOverride = _meshCasterMaterial;
        proxy.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        return proxy;
    }

    private static void SyncMeshProxy(MeshInstance3D source, MeshInstance3D proxy)
    {
        proxy.GlobalTransform = source.GlobalTransform;
        proxy.Mesh = source.Mesh;
    }

    // RenderingServer.GlobalShaderParameterGet is editor-only, so we can't
    // ask the server whether a global is already registered. Track it here
    // instead; the set survives for the process lifetime, matching the
    // lifetime of the globals inside RenderingServer.
    private static readonly HashSet<StringName> _registeredGlobals = new();

    private static void RegisterGlobalParam(string name, RenderingServer.GlobalShaderParameterType type, Variant defaultValue)
    {
        StringName key = name;
        if (_registeredGlobals.Contains(key))
        {
            return;
        }
        RenderingServer.GlobalShaderParameterSet(key, defaultValue);
        _registeredGlobals.Add(key);
    }
}
