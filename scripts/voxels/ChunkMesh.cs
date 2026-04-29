using System;
using Godot;

public partial class ChunkMesh : Node3D
{
    public bool CollisionReady { get; private set; }

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

    private static readonly ShaderMaterial SharedMaterial;
    private static readonly ShaderMaterial BackfaceStencilMaterial;
    private static readonly ShaderMaterial ShadowCasterMaterial;
    private static readonly ShaderMaterial WaterMaterial;
    private static readonly ShaderMaterial WaterBackfaceMaterial;

    static ChunkMesh()
    {
        var shader = GD.Load<Shader>("res://shaders/voxel_clip.gdshader");
        SharedMaterial = new ShaderMaterial();
        SharedMaterial.Shader = shader;
        var tileArray = GD.Load<TextureLayered>("res://assets/textures/voxels/voxel_tiles.png");
        SharedMaterial.SetShaderParameter("tile_array", tileArray);

        // Macro detail overlay + glancing-angle ground normal. Both are
        // low-cost noise textures; the shader defaults for freq/strength live
        // alongside the uniform declarations in voxel_clip.gdshader.
        var detailNoise = GD.Load<Texture2D>("res://assets/textures/voxels/detail_noise.tres");
        SharedMaterial.SetShaderParameter("detail_noise", detailNoise);
        var detailNormal = GD.Load<Texture2D>("res://assets/textures/voxels/detail_normal.tres");
        SharedMaterial.SetShaderParameter("detail_normal", detailNormal);

        // Populate the per-tile variant table. Entry i carries (num_bands,
        // variants_per_band, _, _) for the tile whose base layer is i;
        // unused slots stay at (1,1,0,0) so any accidental index collapses
        // to "no variation". The world-to-UV scale is global (see
        // TILE_UV_SCALE below) — no longer a per-tile value.
        var variantTable = new Godot.Collections.Array();
        for (int i = 0; i < VoxelTypeInfo.TILE_VARIANT_TABLE_SIZE; i++)
        {
            if (VoxelTypeInfo.TileVariants.TryGetValue(i, out var info))
            {
                variantTable.Add(new Vector4(info.Bands, info.VariantsPerBand, 0f, 0f));
            }
            else
            {
                variantTable.Add(new Vector4(1, 1, 0, 0));
            }
        }
        SharedMaterial.SetShaderParameter("tile_variants", variantTable);
        SharedMaterial.SetShaderParameter("tile_uv_scale", VoxelTypeInfo.TILE_UV_SCALE);
        SharedMaterial.SetShaderParameter("band_origin_y", VoxelTypeInfo.TILE_BAND_ORIGIN_Y);
        SharedMaterial.SetShaderParameter("band_height", VoxelTypeInfo.TILE_BAND_HEIGHT);
        SharedMaterial.SetShaderParameter("band_blend", VoxelTypeInfo.TILE_BAND_BLEND);

        var backfaceShader = GD.Load<Shader>("res://shaders/voxel_backface_stencil.gdshader");
        BackfaceStencilMaterial = new ShaderMaterial();
        BackfaceStencilMaterial.Shader = backfaceShader;
        // Last writer in the stencil chain — runs after voxel_water (-3)
        // and water_backface (-2) so its stencil=1 survives wherever the
        // cap should draw. See WaterBackfaceMaterial above for the full
        // priority ladder.
        BackfaceStencilMaterial.RenderPriority = -1;

        var shadowCasterShader = GD.Load<Shader>("res://shaders/voxel_shadow_caster.gdshader");
        ShadowCasterMaterial = new ShaderMaterial();
        ShadowCasterMaterial.Shader = shadowCasterShader;

        var waterShader = GD.Load<Shader>("res://shaders/voxel_water.gdshader");
        WaterMaterial = new ShaderMaterial();
        WaterMaterial.Shader = waterShader;
        // Draw water BEFORE the stencil-write passes (water_backface_stencil
        // at -1 and voxel_backface_stencil at 0) so they can overwrite
        // water's stencil=4 (used for the reflection mask) with their own
        // stencil values (2 / 1) where they're meant to drive caps. Without
        // this, voxel_water's `stencil_mode write, compare_always, 4` runs
        // last and clobbers any stencil=1 the backface pass wrote at the
        // same pixel — the cap then reads compare_equal=1, finds 4, and
        // doesn't draw. Verified by clip_debug 2 + water_hide 1: with water
        // hidden, stencil=1 survives and the cap draws as expected.
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
        // Stencil pipeline order:
        //   -3  voxel_water           writes stencil=4 (reflection mask)
        //   -2  voxel_water_backface  writes stencil=2 (water cap region)
        //   -1  voxel_backface_stencil writes stencil=1 (ceiling cap region) — already 0; bumped via separate field
        //    1  clip_cap              reads  stencil=1 (ceiling cap)
        //    2  water_clip_cap        reads  stencil=2 (water cap)
        // Each stencil writer overwrites earlier values, so backface_stencil
        // wins over water/water_backface in the cap region — exactly what we
        // want, since cap occludes water visually too.
        WaterBackfaceMaterial.RenderPriority = -2;
    }

    // Must match MAX_KITS in voxel_clip.gdshader.
    private const int MAX_KITS = 16;

    // World-scoped detail palette cached statically so ChunkMesh.Create can
    // pass it to ChunkDetailScatter without threading it through every
    // chunk-build call. Set once at world start (Main.StartGame); a future
    // streaming refactor that swaps worlds should re-call SetDetailGroups.
    private static DetailGroupData[] _activeDetailGroups;

    // World-scoped kit palette. Cached alongside _activeDetailGroups so
    // ChunkDetailScatter can resolve each painted voxel's kit to its
    // GroundTint without threading the array through every chunk-build call.
    private static EnvironmentKitData[] _activeKits;
    public static EnvironmentKitData[] ActiveKits => _activeKits;

    // Upload the active world's environment kit palette to the terrain
    // material's uniform arrays. The shader indexes these arrays via the
    // per-vertex KitId packed into CUSTOM1.yzw by the mesher. Call once at
    // world start after WorldGenData is available and before any chunk mesh
    // first renders; subsequent calls are a no-op if kits haven't changed.
    public static void SetKits(EnvironmentKitData[] kits)
    {
        _activeKits = kits;
        // kit_tiles[i] = (flat, wall, _, _). The shader reads .x/.y for the
        //   flat↔wall smoothstep blend. Overlays are authored per-voxel as a
        //   direct tile_array base-layer index (see OverlayId), not owned by
        //   the kit, so .z/.w are reserved.
        // kit_bands[i] = (wall_lo, wall_hi, _, _). One transition:
        //   y < wall_lo → 100% wall; y > wall_hi → 100% flat.
        var tiles = new Vector4[MAX_KITS];
        var bands = new Vector4[MAX_KITS];
        int n = kits != null ? Math.Min(kits.Length, MAX_KITS) : 0;
        for (int i = 0; i < n; i++)
        {
            var kit = kits[i];
            if (kit == null) { continue; }
            tiles[i] = new Vector4(kit.FlatTile, kit.WallTile, 0f, 0f);
            bands[i] = new Vector4(kit.WallBand.X, kit.WallBand.Y, 0f, 0f);
        }
        SharedMaterial.SetShaderParameter("kit_tiles", tiles);
        SharedMaterial.SetShaderParameter("kit_bands", bands);
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
            World.Current?.DetailScatter?.RemoveChunk(_scatteredChunkCoord);
            _scatterPosted = false;
        }
    }

    public static ChunkMesh Create(
        ChunkState data,
        Func<int, int, int, VoxelType> getVoxel,
        Func<int, int, int, VoxelTypeInfo.SharpAxes> getShape,
        Func<int, int, int, int> getKitId,
        Func<int, int, int, int> getOverlayId,
        Func<int, int, int, bool> chunkExists)
    {
        var chunk = new ChunkMesh();
        chunk.Position = new Vector3(
            data.ChunkCoord.X * ChunkState.SIZE,
            data.ChunkCoord.Y * ChunkState.SIZE,
            data.ChunkCoord.Z * ChunkState.SIZE
        );
        chunk.BuildMesh(data, getVoxel, getShape, getKitId, getOverlayId, chunkExists);
        return chunk;
    }

    private void BuildMesh(
        ChunkState data,
        Func<int, int, int, VoxelType> getVoxel,
        Func<int, int, int, VoxelTypeInfo.SharpAxes> getShape,
        Func<int, int, int, int> getKitId,
        Func<int, int, int, int> getOverlayId,
        Func<int, int, int, bool> chunkExists)
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
        st.SetMaterial(SharedMaterial);

        ChunkMesherDC.Build(data, getVoxel, getShape, getKitId, getOverlayId, chunkExists, st, chunkWorldX, chunkWorldY, chunkWorldZ, out bool hasAnyFace);

        // Detail-sprite scatter (grass, flowers, etc.). Compute the per-entry
        // instance contributions and post them to the world-wide manager so
        // every chunk's instances of the same DetailEntry collapse into one
        // MultiMesh draw call. _ExitTree below removes this chunk's
        // contributions when the chunk evicts.
        _scatteredChunkCoord = data.ChunkCoord;
        var scatterContrib = ChunkDetailScatter.Compute(data, getVoxel, getKitId, _activeDetailGroups, _activeKits);
        World.Current?.DetailScatter?.SetChunk(data.ChunkCoord, scatterContrib);
        _scatterPosted = scatterContrib != null;

        // Water (axis-aligned cubic faces)
        var stWater = new SurfaceTool();
        stWater.Begin(Mesh.PrimitiveType.Triangles);
        stWater.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        stWater.SetMaterial(WaterMaterial);

        WaterMesher.Build(data, getVoxel, stWater, chunkWorldX, chunkWorldY, chunkWorldZ, out bool hasAnyWaterFace);

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
            ArrayMesh mesh = st.Commit();

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
            // Shadow-casting is delegated to the shadow-proxy below. The
            // voxel_clip shader discards fragments above camera_clip for
            // the ceiling cutaway, and that discard runs in the shadow
            // pass too — so if this visible mesh cast shadows, terrain
            // above the cutaway would stop throwing shadows down onto
            // the visible interior.
            visual.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            visual.MaterialOverride = SharedMaterial;
            AddChild(visual);

            var backface = new MeshInstance3D();
            backface.Mesh = mesh;
            backface.MaterialOverride = BackfaceStencilMaterial;
            backface.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            AddChild(backface);

            // Non-clipping shadow proxy — casts the full terrain silhouette
            // into the directional shadow atlas regardless of camera_clip.
            var shadowCaster = new MeshInstance3D();
            shadowCaster.Mesh = mesh;
            shadowCaster.MaterialOverride = ShadowCasterMaterial;
            shadowCaster.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
            AddChild(shadowCaster);

            visual.CreateTrimeshCollision();
        }

        if (hasAnyWaterFace)
        {
            ArrayMesh waterMesh = stWater.Commit();

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

            var waterTrigger = new WaterTrigger();
            var waterShape = waterMesh.CreateTrimeshShape();
            var waterCollision = new CollisionShape3D();
            waterCollision.Shape = waterShape;
            waterTrigger.AddChild(waterCollision);
            AddChild(waterTrigger);
        }

        CollisionReady = true;
    }
}
