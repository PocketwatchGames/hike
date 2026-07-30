using System;
using System.Collections.Generic;
using Godot;

public partial class ChunkMesh : Node3D
{
    public bool CollisionReady { get; private set; }

    // DIAGNOSTIC (water-vanish investigation): true when this chunk built a
    // water surface mesh. Lets ChunkManager log streaming load/unload of
    // water-bearing chunks behind the `chunk_water_log` CVar, to confirm
    // whether an outdoor-water vanish coincides with the chunk streaming out.
    public bool HasWater { get; private set; }

    // When non-null, only this chunk coord builds geometry. Set via CVar
    // `debug_only_chunk` for isolating chunks while debugging.
    public static Vector3I? OnlyChunkFilter;

    // Tracks whether this chunk has posted detail-scatter contributions to
    // WorldDetailScatter and at which coord, so _ExitTree can remove them
    // cleanly when the chunk evicts. The manager keeps one MultiMesh per
    // DetailEntry world-wide; without explicit RemoveChunk, evicted chunks'
    // instances would linger in the global multimesh until they're
    // overwritten by a new chunk at the same coord.
    private Vector3I _scatteredChunkCoord;
    private bool _scatterPosted;

    // Lazy-initialized on first ChunkMesh.Create / SetTerrains call rather
    // than from a static constructor. Static cctors fire when Godot walks
    // the script-class registry at editor startup (via the source-gen-
    // emitted GetGodotMethodList / GetGodotPropertyList statics), and at
    // that point user C# resource types may not be registered yet —
    // `GD.Load<BlockCatalog>(...)` would come back as a plain Godot.Resource
    // and the cast throws. Deferring to first use guarantees registration
    // is complete and keeps the cctor from triggering during introspection.
    private static ShaderMaterial SharedMaterial;
    private static ShaderMaterial ShadowCasterMaterial;
    private static ShaderMaterial WaterMaterial;
    private static ShaderMaterial WaterBackfaceMaterial;
    // Materials used by the off-screen cap-mask render. Both apply to the
    // same chunk mesh on CapMaskLayer-only MeshInstance3Ds and produce a
    // black-and-white texture the cap shader samples to discard non-cap
    // pixels.
    private static ShaderMaterial MaskTerrainMaterial;
    private static ShaderMaterial MaskBackfaceMaterial;
    private static bool _materialsInitialized;

    // The terrain color atlas (voxel_tiles.png as a Texture2DArray), kept so
    // SetTerrains can sample each terrain's flat-tile average for its detail
    // GroundTint. Same resource the shader binds to `tile_array`.
    private static TextureLayered _tileColorArray;
    // Per-atlas-layer cached linear-space average color. Keyed by layer index
    // so terrains sharing a flat tile decode the layer only once.
    private static readonly Dictionary<int, Color> _layerAverageCache = new();

    // Baked-AO darkening strength pushed to the terrain shader's `ao_strength`
    // uniform (0 = AO off, 1 = authored, >1 exaggerates for verification).
    // Cached so a CVar set before the material exists still takes effect once
    // EnsureMaterialsInitialized runs. See CVars.aoStrength.
    private static float _aoStrength = 1f;

    public static void SetAoStrength(float value)
    {
        _aoStrength = value;
        if (_materialsInitialized && SharedMaterial != null)
        {
            SharedMaterial.SetShaderParameter("ao_strength", value);
        }
    }

    // Concavity-bake debug visualization toggle (CUSTOM2.w). Diagnostic, kept as
    // a CVar; the concavity pooling tuning (concavity_wetness_strength /
    // concavity_threshold) is now authored on resources/materials/terrain.tres.
    private static bool _debugConcavity = false;

    public static void SetDebugConcavity(bool value)
    {
        _debugConcavity = value;
        if (_materialsInitialized && SharedMaterial != null)
        {
            SharedMaterial.SetShaderParameter("debug_concavity", value);
        }
    }

    // Terrain atlas + wetness tuning (tile_uv_scale, tile_normal_strength, the
    // three blend sharpnesses, wet_displacement/roughness_min/chroma, concavity
    // pooling) is authored on resources/materials/terrain.tres rather than via
    // CVars — see that material. ao_strength stays a CVar because it also feeds
    // the detail-sprite material (DetailEntry), keeping ground + props in lockstep.

    private static void EnsureMaterialsInitialized()
    {
        if (_materialsInitialized)
        {
            return;
        }
        // Set the flag first so a load failure inside this method doesn't
        // re-enter and double-build the materials on the retry path. Any
        // exception below is a real bug to surface, not transient.
        _materialsInitialized = true;

        // Authored base material (shader + author-tunable uniforms with no
        // runtime/CVar owner — the puddle_ripple_* footstep-ripple feel; tune
        // them in resources/materials/terrain.tres). Loaded by path like the
        // shader and texture atlases below — ChunkMesh is the static terrain
        // infrastructure with no upstream scene owner. The runtime-computed
        // params (texture arrays, class/porosity tables) and CVar knobs are
        // pushed onto it below; the authored puddle_ripple_* uniforms are left
        // as-is.
        SharedMaterial = GD.Load<ShaderMaterial>("res://resources/materials/terrain.tres");
        var tileArray = GD.Load<TextureLayered>("res://assets/textures/voxels/voxel_tiles.png");
        _tileColorArray = tileArray;
        // Pre-warm the per-layer average-color cache (used for detail-sprite
        // GroundTint, flat tiles AND per-voxel overlays) on the main thread at
        // load, so the scatter — which reads it per painted voxel and may run
        // off-thread in future — only ever hits the populated cache.
        for (int layer = 0; layer < tileArray.GetLayers(); layer++)
        {
            TryGetLayerAverageLinear(layer, out _);
        }
        SharedMaterial.SetShaderParameter("tile_array", tileArray);
        // Seed AO darkening strength (honors any CVar set before this ran).
        // Stays a CVar — DetailEntry feeds the same value to detail sprites so
        // ground and props darken in lockstep.
        SharedMaterial.SetShaderParameter("ao_strength", _aoStrength);
        // Concavity-bake debug viz toggle (CVar; the pooling tuning is authored
        // on the material).
        SharedMaterial.SetShaderParameter("debug_concavity", _debugConcavity);

        // Packed per-tile normal (RGB) + height (A) atlas, sampled alongside
        // the color atlas (both nearest-filtered).
        var nrmHeight = GD.Load<TextureLayered>("res://assets/textures/voxels/voxel_tiles_nrm_height.png");
        SharedMaterial.SetShaderParameter("tile_nrm_height", nrmHeight);

        // tile_uv_scale, tile_normal_strength, the blend sharpnesses, the wet_*
        // model params and concavity pooling are authored on terrain.tres — not
        // re-pushed here, so the material's values win.

        // Per-atlas-layer cliff/ground class (BlockData.IsCliff) and wetness
        // porosity (BlockData.Porosity), for the shader's height-blend routing
        // and wet-look split. Both indexed by AtlasBaseIndex.
        var cliffTable = new Godot.Collections.Array();
        var porosityTable = new Godot.Collections.Array();
        for (int i = 0; i < VoxelTypeInfo.MAX_ATLAS_LAYERS; i++)
        {
            BlockData block = BlockCatalog.Active.GetByAtlasIndex(i);
            cliffTable.Add((block != null && block.isCliff) ? 1f : 0f);
            porosityTable.Add(block != null ? block.porosity : 0.5f);
        }
        SharedMaterial.SetShaderParameter("tile_is_cliff", cliffTable);
        SharedMaterial.SetShaderParameter("tile_porosity", porosityTable);

        var shadowCasterShader = GD.Load<Shader>("res://shaders/voxel_shadow_caster.gdshader");
        ShadowCasterMaterial = new ShaderMaterial();
        ShadowCasterMaterial.Shader = shadowCasterShader;

        var maskTerrainShader = GD.Load<Shader>("res://shaders/cap_mask_terrain.gdshader");
        MaskTerrainMaterial = new ShaderMaterial();
        MaskTerrainMaterial.Shader = maskTerrainShader;

        var maskBackfaceShader = GD.Load<Shader>("res://shaders/cap_mask_backface.gdshader");
        MaskBackfaceMaterial = new ShaderMaterial();
        MaskBackfaceMaterial.Shader = maskBackfaceShader;
        // Renders after the front-face mask material so its depth test
        // sees the front-face's depth — only back-faces NOT occluded by a
        // visible front-face (i.e., clipped zones and underground
        // overdraw recovery) write white.
        MaskBackfaceMaterial.RenderPriority = 1;

        var waterShader = GD.Load<Shader>("res://shaders/voxel_water.gdshader");
        WaterMaterial = new ShaderMaterial();
        WaterMaterial.Shader = waterShader;
        // Renders before water_backface_stencil so the back-face's
        // stencil=2 write isn't clobbered by water's stencil=4
        // (reflection mask) at coplanar pixels.
        WaterMaterial.RenderPriority = -3;
        // Two pre-baked normal-map textures for ripple perturbation. Each is
        // a NoiseTexture2D with as_normal_map=true (Godot bakes the noise
        // gradient into RGB). Different seeds/frequencies so when the shader
        // combines their decoded normals the result doesn't tile.
        var rippleA = GD.Load<Texture2D>("res://assets/textures/water_ripple_a.tres");
        var rippleB = GD.Load<Texture2D>("res://assets/textures/water_ripple_b.tres");
        WaterMaterial.SetShaderParameter("ripple_tex_a", rippleA);
        WaterMaterial.SetShaderParameter("ripple_tex_b", rippleB);

        var waterBackfaceShader = GD.Load<Shader>("res://shaders/voxel_water_backface.gdshader");
        WaterBackfaceMaterial = new ShaderMaterial();
        WaterBackfaceMaterial.Shader = waterBackfaceShader;
        // Render order in the main scene (cap mask builds its own pipeline
        // off-screen via the SubViewport — see GameCamera + cap_mask_*
        // shaders). voxel_water_backface still runs in the main scene to
        // write stencil=2 for the water_clip_cap, which keeps its
        // stencil-driven design.
        //   -3  voxel_water           writes stencil=4 (reflection mask)
        //   -2  voxel_water_backface  writes stencil=2 (water cap zone)
        //    0  voxel_clip / sprites  default priority
        //    1  clip_cap              opaque, samples cap mask via SCREEN_UV
        //    2  water_clip_cap        alpha, reads stencil=2
        WaterBackfaceMaterial.RenderPriority = -2;
    }

    // Must match MAX_TERRAINS in voxel_clip.gdshader.
    private const int MAX_TERRAINS = 16;

    // World-scoped detail palette cached statically so ChunkMesh.Create can
    // pass it to ChunkDetailScatter without threading it through every
    // chunk-build call. Set once at world start (Main.StartGame).
    private static DetailGroupData[] _activeDetailGroups;
    public static DetailGroupData[] ActiveDetailGroups => _activeDetailGroups;

    // World-scoped kit palette. Cached alongside _activeDetailGroups so
    // ChunkDetailScatter can resolve each painted voxel's kit to its
    // GroundTint without threading the array through every chunk-build call.
    private static TerrainData[] _activeTerrains;
    public static TerrainData[] ActiveTerrains => _activeTerrains;

    // Upload the active world's environment kit palette to the terrain
    // material's uniform arrays. The shader indexes these arrays via the
    // per-vertex TerrainId packed into CUSTOM1.yzw by the mesher. Call once at
    // world start after WorldGenData is available and before any chunk mesh
    // first renders; subsequent calls are a no-op if kits haven't changed.
    public static void SetTerrains(TerrainData[] terrains)
    {
        EnsureMaterialsInitialized();
        _activeTerrains = terrains;
        // terrain_tiles[i] = (flat, wall, _, _). The shader reads .x/.y for
        //   the flat↔wall smoothstep blend. Overlays are authored per-voxel
        //   as a direct tile_array base-layer index (see OverlayId), not
        //   owned by the terrain, so .z/.w are reserved.
        // terrain_bands[i] = (wall_lo, wall_hi, _, _). One transition:
        //   y < wall_lo → 100% wall; y > wall_hi → 100% flat.
        var tiles = new Vector4[MAX_TERRAINS];
        var bands = new Vector4[MAX_TERRAINS];
        int n = terrains != null ? Math.Min(terrains.Length, MAX_TERRAINS) : 0;
        for (int i = 0; i < n; i++)
        {
            var terrain = terrains[i];
            if (terrain == null) { continue; }
            int flat = terrain.flatTile != null ? terrain.flatTile.atlasBaseIndex : BlockCatalog.Active.GetAtlasIndexByName("GrassTop");
            int wall = terrain.wallTile != null ? terrain.wallTile.atlasBaseIndex : BlockCatalog.Active.GetAtlasIndexByName("Stone");
            tiles[i] = new Vector4(flat, wall, 0f, 0f);
            bands[i] = new Vector4(terrain.wallBand.X, terrain.wallBand.Y, 0f, 0f);

            // Detail-sprite ground tint = the average color of the exact flat
            // tile the shader renders for this terrain, so grass roots blended
            // toward it (the tint map's G channel) match the ground beneath
            // them. Computed at load from the atlas rather than hand-authored,
            // so re-importing a tile texture keeps the tint in sync with no
            // bake step. Falls back to the authored GroundTint if the layer
            // can't be decoded (logged once in TryGetLayerAverageLinear).
            if (TryGetLayerAverageLinear(flat, out Color flatAverage))
            {
                terrain.groundTint = flatAverage;
            }
        }
        SharedMaterial.SetShaderParameter("terrain_tiles", tiles);
        SharedMaterial.SetShaderParameter("terrain_bands", bands);
    }

    // Average color of one atlas layer in voxel_tiles.png, in LINEAR space.
    //
    // Color space matters: the shader binds tile_array as `source_color`, so it
    // sees each texel decoded sRGB->linear, and the detail sprite consumes this
    // tint as linear albedo (MultiMesh instance COLOR is passed through without
    // conversion). Image.GetPixel returns the stored sRGB-encoded value, so we
    // decode each texel before accumulating — averaging in linear space is the
    // physically correct mean reflectance and is what makes the rooted grass
    // base match the lit terrain. Alpha-weighted so any transparent padding
    // texels don't drag the average toward black.
    //
    // Cached per layer: terrains sharing a flat tile decode it once. The atlas
    // is small and this runs once per world load, so the cost is negligible.
    internal static bool TryGetLayerAverageLinear(int layer, out Color average)
    {
        average = Colors.White;
        if (_layerAverageCache.TryGetValue(layer, out average))
        {
            return true;
        }
        if (_tileColorArray == null || layer < 0 || layer >= _tileColorArray.GetLayers())
        {
            return false;
        }

        Image img = _tileColorArray.GetLayerData(layer);
        if (img == null)
        {
            GD.PushWarning($"ChunkMesh: could not read tile_array layer {layer} for GroundTint; keeping authored tint.");
            return false;
        }
        // The imported atlas is VRAM-compressed; decode to RGBA before sampling.
        if (img.IsCompressed() && img.Decompress() != Error.Ok)
        {
            GD.PushWarning($"ChunkMesh: could not decompress tile_array layer {layer} for GroundTint; keeping authored tint.");
            return false;
        }

        int w = img.GetWidth();
        int h = img.GetHeight();
        double r = 0.0;
        double g = 0.0;
        double b = 0.0;
        double weight = 0.0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color texel = img.GetPixel(x, y);
                Color lin = texel.SrgbToLinear();
                double a = texel.A;
                r += lin.R * a;
                g += lin.G * a;
                b += lin.B * a;
                weight += a;
            }
        }
        if (weight <= 0.0)
        {
            return false;
        }

        average = new Color((float)(r / weight), (float)(g / weight), (float)(b / weight), 1f);
        _layerAverageCache[layer] = average;
        return true;
    }

    // Detail-sprite palette for ChunkDetailScatter. Index 0 of the per-voxel
    // DetailGroup channel means "no detail", so groups[0] is referenced as
    // DetailGroup=1. See DetailGroupData / ChunkDetailScatter for the scatter
    // contract.
    public static void SetDetailGroups(DetailGroupData[] groups)
    {
        _activeDetailGroups = groups;
    }

    public override void _ExitTree()
    {
        // Pull this chunk's contributions out of the global detail-scatter
        // manager so they don't linger in the multimesh after eviction.
        // World may already be torn down on game shutdown — guard accordingly.
        if (_scatterPosted)
        {
            Sim.Current?.DetailScatter?.RemoveChunk(_scatteredChunkCoord);
            _scatterPosted = false;
        }
    }

    // buildCollision / buildDetails default true for normal chunks. The
    // bird's-eye overlook loads its far backdrop ring with both false: the
    // player is movement-locked in the tree so nothing walks on those chunks
    // (no trimesh collision / water trigger needed), and at the zoomed-out
    // scale the sub-metre detail sprites are sub-pixel (skip the scatter to
    // save the multimesh rebuild + memory). They render mesh + prop scatter
    // only, and unload when the overview ends.
    public static ChunkMesh Create(
        ChunkState data,
        Func<int, int, int, VoxelType> getVoxel,
        Func<int, int, int, VoxelTypeInfo.SharpAxes> getShape,
        Func<int, int, int, int> getTerrainId,
        Func<int, int, int, int> getOverlayId,
        Func<int, int, int, int> getSunlight,
        Func<int, int, int, bool> getSunOpaque,
        Func<int, int, int, bool> chunkExists,
        bool buildCollision = true,
        bool buildDetails = true)
    {
        using var _prof = Profiler.Sample("ChunkMesh.Create");
        EnsureMaterialsInitialized();
        var chunk = new ChunkMesh();
        chunk.Position = new Vector3(
            data.ChunkCoord.X * ChunkState.SIZE,
            data.ChunkCoord.Y * ChunkState.SIZE,
            data.ChunkCoord.Z * ChunkState.SIZE
        );
        chunk.BuildMesh(data, getVoxel, getShape, getTerrainId, getOverlayId, getSunlight, getSunOpaque, chunkExists, buildCollision, buildDetails);
        return chunk;
    }

    private void BuildMesh(
        ChunkState data,
        Func<int, int, int, VoxelType> getVoxel,
        Func<int, int, int, VoxelTypeInfo.SharpAxes> getShape,
        Func<int, int, int, int> getTerrainId,
        Func<int, int, int, int> getOverlayId,
        Func<int, int, int, int> getSunlight,
        Func<int, int, int, bool> getSunOpaque,
        Func<int, int, int, bool> chunkExists,
        bool buildCollision,
        bool buildDetails)
    {
        if (OnlyChunkFilter.HasValue && data.ChunkCoord != OnlyChunkFilter.Value)
        {
            CollisionReady = true;
            return;
        }

        int chunkWorldX = data.ChunkCoord.X * ChunkState.SIZE;
        int chunkWorldY = data.ChunkCoord.Y * ChunkState.SIZE;
        int chunkWorldZ = data.ChunkCoord.Z * ChunkState.SIZE;

        // Terrain (Dual Contouring)
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        // CUSTOM1: (sharpness, kit_a, kit_b, kit_c). .x drives smooth-vs-flat
        // shading; .yzw is the triangle's three corner kit ids (constant across
        // the tri so the shader can barycentric-pick, same pattern as tile ids).
        st.SetCustomFormat(1, SurfaceTool.CustomFormat.RgbaFloat);
        // CUSTOM2: (overlay_a, overlay_b, overlay_c, _). Per-corner authored
        // overlay ids for the AUTO terrain branch.
        st.SetCustomFormat(2, SurfaceTool.CustomFormat.RgbaFloat);
        // CUSTOM3: (baked_sun, _, _, _). Per-vertex sun read from the air the
        // surface faces — see ChunkMesherDC.BakeVertexSun.
        st.SetCustomFormat(3, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetMaterial(SharedMaterial);

        bool hasAnyFace;
        using (Profiler.Sample("ChunkMesh.MesherDC"))
        {
            ChunkMesherDC.Build(data, getVoxel, getShape, getTerrainId, getOverlayId, getSunlight, getSunOpaque, chunkExists, st, chunkWorldX, chunkWorldY, chunkWorldZ, out hasAnyFace);
        }

        // Detail-sprite scatter (grass, flowers, etc.). Compute the per-entry
        // instance contributions and post them to the world-wide manager so
        // every chunk's instances of the same DetailEntry collapse into one
        // MultiMesh draw call. _ExitTree below removes this chunk's
        // contributions when the chunk evicts.
        if (buildDetails)
        {
            _scatteredChunkCoord = data.ChunkCoord;
            Dictionary<DetailEntry, List<ChunkDetailScatter.InstanceData>> scatterContrib;
            using (Profiler.Sample("ChunkMesh.DetailScatter"))
            {
                scatterContrib = ChunkDetailScatter.Compute(data, getVoxel, getTerrainId, _activeDetailGroups, _activeTerrains);
            }
            Sim.Current?.DetailScatter?.SetChunk(data.ChunkCoord, scatterContrib);
            _scatterPosted = scatterContrib != null;
        }

        // Water (axis-aligned cubic faces)
        var stWater = new SurfaceTool();
        stWater.Begin(Mesh.PrimitiveType.Triangles);
        stWater.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        stWater.SetMaterial(WaterMaterial);

        bool hasAnyWaterFace;
        using (Profiler.Sample("ChunkMesh.WaterMesher"))
        {
            WaterMesher.Build(data, getVoxel, stWater, chunkWorldX, chunkWorldY, chunkWorldZ, out hasAnyWaterFace);
        }

        if (!hasAnyFace && !hasAnyWaterFace)
        {
            CollisionReady = true;
            return;
        }

        if (hasAnyFace)
        {
            // Normals are authored per-vertex by the mesher from the 8-corner
            // density gradient. Don't call GenerateNormals — run per-chunk it
            // would average only the owner chunk's triangles at boundary
            // vertices, producing normals that disagree with the neighbour's
            // and a visible slope-pick seam along chunk borders.
            ArrayMesh mesh;
            using (Profiler.Sample("ChunkMesh.Commit"))
            {
                mesh = st.Commit();
            }

            if (ChunkMesherDC.DebugLog)
            {
                Aabb aabb = mesh.GetAabb();
                int vertCount = 0;
                if (mesh.GetSurfaceCount() > 0)
                {
                    var arrays = mesh.SurfaceGetArrays(0);
                    var verts = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
                    vertCount = verts?.Length ?? 0;
                }
                GD.Print($"[DC] chunk {data.ChunkCoord} verts={vertCount} aabb={aabb.Position}..{aabb.End} nodePos={Position}");
            }

            var visual = new MeshInstance3D();
            visual.Mesh = mesh;
            visual.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            visual.MaterialOverride = SharedMaterial;
            visual.Layers = GameCamera.MainSceneLayer;
            AddChild(visual);

            // Cap-mask front-face: BLACK over visible (below-clip) terrain,
            // discarded above clip. The white SubViewport clear shows
            // through above-clip discards = cap zone.
            var maskFront = new MeshInstance3D();
            maskFront.Mesh = mesh;
            maskFront.MaterialOverride = MaskTerrainMaterial;
            maskFront.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            maskFront.Layers = GameCamera.CapMaskLayer;
            AddChild(maskFront);

            // Cap-mask back-face: WHITE wherever the back-face passes its
            // depth test. Needed so underground front-faces (rendered
            // through other clipped solids) don't paint black across the
            // cap zone — the back-face writes white over them, restoring
            // the cap mask.
            var maskBack = new MeshInstance3D();
            maskBack.Mesh = mesh;
            maskBack.MaterialOverride = MaskBackfaceMaterial;
            maskBack.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            maskBack.Layers = GameCamera.CapMaskLayer;
            AddChild(maskBack);

            // Non-clipping shadow proxy — casts the full terrain silhouette
            // into the directional shadow atlas regardless of camera_clip.
            var shadowCaster = new MeshInstance3D();
            shadowCaster.Mesh = mesh;
            shadowCaster.MaterialOverride = ShadowCasterMaterial;
            shadowCaster.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
            AddChild(shadowCaster);

            if (buildCollision)
            {
                using (Profiler.Sample("ChunkMesh.TrimeshCollision"))
                {
                    visual.CreateTrimeshCollision();
                }
            }
        }

        HasWater = hasAnyWaterFace;
        if (hasAnyWaterFace)
        {
            ArrayMesh waterMesh;
            using (Profiler.Sample("ChunkMesh.WaterCommit"))
            {
                waterMesh = stWater.Commit();
            }

            var waterVisual = new MeshInstance3D();
            waterVisual.Mesh = waterMesh;
            waterVisual.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

            var waterMat = WaterMaterial.Duplicate() as ShaderMaterial;
            waterVisual.MaterialOverride = waterMat;
            AddChild(waterVisual);

            var waterBackface = new MeshInstance3D();
            waterBackface.Mesh = waterMesh;
            waterBackface.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            waterBackface.MaterialOverride = WaterBackfaceMaterial;
            AddChild(waterBackface);

            if (buildCollision)
            {
                using (Profiler.Sample("ChunkMesh.WaterTrimeshCollision"))
                {
                    var waterTrigger = new WaterTrigger();
                    var waterShape = waterMesh.CreateTrimeshShape();
                    var waterCollision = new CollisionShape3D();
                    waterCollision.Shape = waterShape;
                    waterTrigger.AddChild(waterCollision);
                    AddChild(waterTrigger);
                }
            }
        }

        CollisionReady = true;
    }
}
