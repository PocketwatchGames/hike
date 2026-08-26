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
    // `GD.Load<BlockSurfaceCatalog>(...)` would come back as a plain Godot.Resource
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
    // GroundTintFor samples a block's top-surface average from it. Same
    // resource the shader binds to `tile_array`.
    private static TextureLayered _tileColorArray;
    // Per-atlas-layer cached linear-space average color. Keyed by layer index
    // so terrains sharing a flat tile decode the layer only once.
    private static readonly Dictionary<int, Color> _layerAverageCache = new();

    // Layers already known to be unreadable. Failures MUST be memoized like
    // successes: this runs per scattered detail sprite, not once per world load,
    // so an uncached miss re-probes the atlas for every sprite in every chunk.
    private static readonly HashSet<int> _layerAverageFailed = new();
    private static bool _groundTintWarned;

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

    // Overlay-coverage debug visualization. See debug_overlay_cov in the shader.
    private static bool _debugOverlayCov = false;

    public static void SetDebugOverlayCov(bool value)
    {
        _debugOverlayCov = value;
        if (_materialsInitialized && SharedMaterial != null)
        {
            SharedMaterial.SetShaderParameter("debug_overlay_cov", value);
        }
    }

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

    private const string TerrainMaterialPath = "res://resources/materials/terrain.tres";

    // Public because the parallel fill must force it before any worker can
    // race into it — it touches the rendering server.
    public static void EnsureMaterialsInitialized()
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
        SharedMaterial = GD.Load<ShaderMaterial>(TerrainMaterialPath);
        var tileArray = GD.Load<TextureLayered>("res://assets/textures/terrain/voxel_tiles.png");
        _tileColorArray = tileArray;
        // Pre-warm the per-layer average-color cache (used for detail-sprite
        // GroundTint, flat tiles AND per-voxel overlays) on the main thread at
        // load, so the scatter — which reads it per painted voxel and may run
        // off-thread in future — only ever hits the populated cache.
        for (int layer = 0; layer < tileArray.GetLayers(); layer++)
        {
            TryGetLayerAverageLinear(layer, out _);
        }

        // Packed per-tile normal (RGB) + height (A) atlas, sampled alongside
        // the color atlas (both nearest-filtered).
        var nrmHeight = GD.Load<TextureLayered>("res://assets/textures/terrain/voxel_tiles_nrm_height.png");
        PushRuntimeMaterialParams(tileArray, nrmHeight);

        // tile_uv_scale, tile_normal_strength, the blend sharpnesses, the wet_*
        // model params and concavity pooling are authored on terrain.tres — not
        // re-pushed here, so the material's values win.

        // Per-atlas-layer cliff/ground class (BlockSurfaceData.IsCliff) and wetness
        // porosity (BlockSurfaceData.Porosity), for the shader's height-blend routing
        // and wet-look split. Both indexed by AtlasBaseIndex.
        // Per-atlas-layer mean height, measured from the atlas rather than
        // authored — source height maps sit at very different means (grass 0.23,
        // cobblestone 0.53), and the cavity term subtracts this so it darkens
        // pits without dimming whole tiles by that arbitrary offset. Derived at
        // load like GroundTint above, so re-baking a height map keeps it in sync
        // with no authoring step.

        // Per-block face/band tables. Global and static, so unlike the old
        // per-world terrain palette this is uploaded once here rather than
        // re-pushed at every world load.
        UploadBlockTables();

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
        UploadWaterBlockTables(tileArray, nrmHeight);

        var waterBackfaceShader = GD.Load<Shader>("res://shaders/voxel_water_backface.gdshader");
        WaterBackfaceMaterial = new ShaderMaterial();
        WaterBackfaceMaterial.Shader = waterBackfaceShader;
        // Render order in the main scene (cap mask builds its own pipeline
        // off-screen via the SubViewport — see GameCamera + cap_mask_*
        // shaders). voxel_water_backface still runs in the main scene to
        // write stencil=2 for the water_clip_cap, which keeps its
        // stencil-driven design.
        //   -6  waterfall             DEPTH_DRAW_ALWAYS, and first on purpose:
        //                             the pool depth-tests against the sheet,
        //                             which is the only thing that sorts a fall
        //                             against water it can be in front of or
        //                             behind (see waterfall.gdshader)
        //   -3  voxel_water           DEPTH_DRAW_ALWAYS so water sorts against
        //                             water; writes stencil=4 (reflection mask)
        //   -2  voxel_water_backface  writes stencil=2 (water cap zone)
        //   -1  waterfall_drawdown    the flat run over the pool; composites with
        //                             it and writes NO depth, the opposite of the
        //                             sheet and for the opposite reason
        //    0  voxel_clip / sprites  default priority
        //    1  clip_cap              opaque, samples cap mask via SCREEN_UV
        //    2  water_clip_cap        alpha, reads stencil=2
        WaterBackfaceMaterial.RenderPriority = -2;
    }

    // World-scoped detail palette cached statically so ChunkMesh.Create can
    // pass it to ChunkDetailScatter without threading it through every
    // chunk-build call. Set once at world start (Main.StartGame).
    private static DetailGroupData[] _activeDetailGroups;
    public static DetailGroupData[] ActiveDetailGroups => _activeDetailGroups;

    // Upload the global block catalog's face/band tables. Indexed by the
    // per-vertex block id the mesher packs into CUSTOM0.xyz.
    //   block_faces[i] = (top, side, bottom, _) atlas layers, each resolved
    //     through BlockData.SurfaceFor so a block authoring only a top wears it
    //     on every slot and the blend collapses to one sample.
    //   block_bands[i] = (lo, hi, _, _) — the smoothstep on |normal.y|.
    // Everything the terrain material carries that is NOT authored on
    // terrain.tres. Kept in one place because reloading that .tres resets the
    // material to its authored state, so every runtime value has to go back on.
    private static void PushRuntimeMaterialParams(TextureLayered tileArray, TextureLayered nrmHeight)
    {
        SharedMaterial.SetShaderParameter("tile_array", tileArray);
        SharedMaterial.SetShaderParameter("tile_nrm_height", nrmHeight);
        // AO stays a CVar — DetailEntry feeds the same value to detail sprites so
        // ground and props darken in lockstep.
        SharedMaterial.SetShaderParameter("ao_strength", _aoStrength);
        SharedMaterial.SetShaderParameter("debug_concavity", _debugConcavity);
        SharedMaterial.SetShaderParameter("debug_overlay_cov", _debugOverlayCov);
        UploadSurfaceTables(tileArray, nrmHeight);
        UploadBlockTables();
    }

    // Per-atlas-layer tables: everything the shader indexes by atlasBaseIndex.
    // Split out from material init so `surface_reload` can re-push it against
    // freshly re-read .tres without rebuilding anything else.
    private static void UploadSurfaceTables(TextureLayered tileArray, TextureLayered nrmHeight)
    {
        var porosityTable = new Godot.Collections.Array();
        var heightMidTable = new Godot.Collections.Array();
        var overlayCliffTable = new Godot.Collections.Array();
        // Sub-voxel overlay edge shaping. Identity defaults for a layer that
        // authors nothing, and for one no surface claims at all.
        var overlayErodeGroundTable = new Godot.Collections.Array();
        var overlayErodeCliffTable = new Godot.Collections.Array();
        var overlayFeatherTable = new Godot.Collections.Array();
        var overlayReliefTable = new Godot.Collections.Array();
        for (int i = 0; i < BlockCatalog.MAX_ATLAS_LAYERS; i++)
        {
            BlockSurfaceData surface = BlockCatalog.Active.GetSurfaceByLayer(i);
            porosityTable.Add(surface != null ? surface.porosity : 0.5f);
            heightMidTable.Add(GetLayerHeightMid(nrmHeight, i));
            overlayCliffTable.Add(surface != null && surface.overlayOnCliffs ? 1f : 0f);
            overlayErodeGroundTable.Add(surface != null ? surface.overlayErodeGround : 0f);
            overlayErodeCliffTable.Add(surface != null ? surface.overlayErodeCliff : 0f);
            overlayFeatherTable.Add(surface != null ? surface.overlayFeather : 1f);
            overlayReliefTable.Add(surface != null ? surface.overlayRelief : 1f);
        }
        SharedMaterial.SetShaderParameter("tile_porosity", porosityTable);
        SharedMaterial.SetShaderParameter("tile_height_mid", heightMidTable);
        SharedMaterial.SetShaderParameter("tile_overlay_cliff", overlayCliffTable);
        SharedMaterial.SetShaderParameter("tile_overlay_erode_ground", overlayErodeGroundTable);
        SharedMaterial.SetShaderParameter("tile_overlay_erode_cliff", overlayErodeCliffTable);
        SharedMaterial.SetShaderParameter("tile_overlay_feather", overlayFeatherTable);
        SharedMaterial.SetShaderParameter("tile_overlay_relief", overlayReliefTable);
        UploadWaterBlockTables(tileArray, nrmHeight);
    }

    // Everything the water material carries that is indexed rather than global:
    // the per-BLOCK turbidity table (read by the shader against the block id the
    // mesher packs into CUSTOM0.x) and the per-LAYER film tuning, plus the atlas
    // itself. Same resources SharedMaterial holds, so this costs no extra VRAM.
    // Same resources SharedMaterial holds, so this costs no extra VRAM.
    //
    // Skipped until the water material exists — EnsureMaterialsInitialized
    // pushes the runtime params before it builds WaterMaterial, and calls this
    // again once it has.
    private static void UploadWaterBlockTables(TextureLayered tileArray, TextureLayered nrmHeight)
    {
        if (WaterMaterial == null)
        {
            return;
        }
        WaterMaterial.SetShaderParameter("tile_array", tileArray);
        WaterMaterial.SetShaderParameter("tile_nrm_height", nrmHeight);

        // Per water BLOCK: turbidity, and the film it wears. A block with no
        // WaterFilmData gets layer 0, which the shader reads as "bare water" —
        // that is the only thing distinguishing "no film" from a film whose
        // opacity happens to be zero.
        var turbidity = new float[BlockCatalog.MAX_BLOCKS];
        var filmA = new Vector4[BlockCatalog.MAX_BLOCKS];
        var filmB = new Vector4[BlockCatalog.MAX_BLOCKS];
        var filmTint = new Vector4[BlockCatalog.MAX_BLOCKS];
        for (int id = 0; id < BlockCatalog.MAX_BLOCKS; id++)
        {
            turbidity[id] = Blocks.WaterTurbidity(id);
            filmTint[id] = new Vector4(1f, 1f, 1f, 1f);
            WaterFilmData film = BlockCatalog.Active.GetById(id)?.waterFilm;
            if (film?.surface == null || film.surface.atlasBaseIndex <= 0)
            {
                continue;
            }
            filmA[id] = new Vector4(film.surface.atlasBaseIndex, film.scale, film.drift, film.opacity);
            filmB[id] = new Vector4(film.breakupScale, film.breakup, film.shape, film.edgeSoftness);
            filmTint[id] = new Vector4(film.tint.R, film.tint.G, film.tint.B, 1f);
        }
        WaterMaterial.SetShaderParameter("water_block_turbidity", turbidity);
        WaterMaterial.SetShaderParameter("water_film_a", filmA);
        WaterMaterial.SetShaderParameter("water_film_b", filmB);
        WaterMaterial.SetShaderParameter("water_film_tint", filmTint);
        WaterMaterial.SetShaderParameter("water_film_force_block", _waterFilmForceBlock);
    }

    // Debug: draw every water surface as one BLOCK, so a film can be judged
    // without regenerating a world to stamp it. -1 = off.
    private static int _waterFilmForceBlock = -1;

    public static void SetWaterFilmForceBlock(int blockId)
    {
        _waterFilmForceBlock = blockId;
        WaterMaterial?.SetShaderParameter("water_film_force_block", blockId);
    }

    // Live tuning hook for the `surface_reload` console command: re-reads every
    // surface .tres AND both atlas strips from disk, then re-pushes the per-layer
    // tables. Edit a value (or re-stitch the atlas) and see it without a restart.
    //
    // CacheMode.Replace updates the EXISTING cached resource in place rather than
    // handing back a new instance, so BlockCatalog's references stay valid and
    // nothing has to be rebound. Only visual per-layer properties are re-read —
    // block ids, Blocks' flattened tables and the meshed geometry are untouched,
    // which is why this needs no re-mesh and cannot desync the sim.
    public static void ReloadSurfaceTables()
    {
        // Deliberately does NOT force material init: there is nothing to re-push
        // before a world has built the terrain material, and doing it from
        // Main._Ready runs the loads far too early to succeed.
        if (!_materialsInitialized || SharedMaterial == null)
        {
            GD.Print("[surface_reload] no terrain material yet — start a game first.");
            return;
        }

        var seen = new HashSet<string>();
        for (int i = 0; i < BlockCatalog.MAX_ATLAS_LAYERS; i++)
        {
            BlockSurfaceData surface = BlockCatalog.Active.GetSurfaceByLayer(i);
            string path = surface?.ResourcePath;
            if (!string.IsNullOrEmpty(path) && seen.Add(path))
            {
                ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Replace);
            }
        }

        var tileArray = ResourceLoader.Load<TextureLayered>(
            "res://assets/textures/terrain/voxel_tiles.png", "", ResourceLoader.CacheMode.Replace);
        var nrmHeight = ResourceLoader.Load<TextureLayered>(
            "res://assets/textures/terrain/voxel_tiles_nrm_height.png", "", ResourceLoader.CacheMode.Replace);
        _tileColorArray = tileArray;
        // Averages are measured from the atlas, so a re-stitch invalidates them —
        // the negative cache included, since a re-stitch may make a layer readable.
        _layerAverageCache.Clear();
        _layerAverageFailed.Clear();
        _groundTintWarned = false;

        // The material itself, so the authored blend tuning on terrain.tres —
        // overlay_blend_sharpness, height_relief_strength, the wet model — is live
        // too. Replace updates the cached resource in place, so SharedMaterial
        // still points at it; but it comes back in its AUTHORED state, which is
        // why every runtime parameter is pushed again below.
        ResourceLoader.Load(TerrainMaterialPath, "", ResourceLoader.CacheMode.Replace);
        PushRuntimeMaterialParams(tileArray, nrmHeight);
        GD.Print($"[surface_reload] re-read {seen.Count} surfaces, the atlas and terrain.tres");
    }

    private static void UploadBlockTables()
    {
        var faces = new Vector4[BlockCatalog.MAX_BLOCKS];
        var bands = new Vector4[BlockCatalog.MAX_BLOCKS];
        BlockCatalog catalog = BlockCatalog.Active;
        for (int id = 0; id < BlockCatalog.MAX_BLOCKS; id++)
        {
            BlockData block = catalog.GetById(id);
            if (block == null || block.IsInvisible())
            {
                continue;
            }
            faces[id] = new Vector4(
                LayerOf(block.SurfaceFor(EBlockFace.Top)),
                LayerOf(block.SurfaceFor(EBlockFace.Side)),
                LayerOf(block.SurfaceFor(EBlockFace.Bottom)),
                0f);
            bands[id] = new Vector4(block.wallBand.X, block.wallBand.Y, 0f, 0f);
        }
        SharedMaterial.SetShaderParameter("block_faces", faces);
        SharedMaterial.SetShaderParameter("block_bands", bands);
    }

    // Mean colour of a block's top surface, for rooting detail sprites into the
    // ground they sit on. Measured from the atlas at load rather than authored,
    // so re-importing a tile keeps it in sync with no bake step.
    public static Color GroundTintFor(int blockId)
    {
        BlockData block = BlockCatalog.Active.GetById(blockId);
        BlockSurfaceData top = block?.SurfaceFor(EBlockFace.Top);
        if (top != null && TryGetLayerAverageLinear(top.atlasBaseIndex, out Color average))
        {
            return average;
        }
        return new Color(0.4f, 0.4f, 0.4f);
    }

    private static float LayerOf(BlockSurfaceData surface)
    {
        return surface != null ? surface.atlasBaseIndex : 0f;
    }

    // Kept as the explicit "the world is starting, build the terrain material
    // now" hook. The block tables it uploads are global, so unlike the old
    // per-world terrain palette there is nothing world-specific to pass in.
    public static void SetTerrains()
    {
        EnsureMaterialsInitialized();
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
    // One line per run, not one per sprite. Both failure modes mean the same
    // thing — this atlas layer cannot be read back on the CPU — and under the
    // dummy renderer that is expected and benign (authored tints are kept), so
    // the per-layer detail is not worth a stack trace apiece.
    private static void WarnGroundTintUnavailable(int layer, string what)
    {
        _layerAverageFailed.Add(layer);
        if (_groundTintWarned)
        {
            return;
        }
        _groundTintWarned = true;
        GD.PushWarning($"ChunkMesh: {what} tile_array layer {layer} for GroundTint; keeping authored tints. "
            + "Expected headless (no texture-array CPU readback); further layers are silent.");
    }

    internal static bool TryGetLayerAverageLinear(int layer, out Color average)
    {
        average = Colors.White;
        if (_layerAverageCache.TryGetValue(layer, out average))
        {
            return true;
        }
        if (_layerAverageFailed.Contains(layer))
        {
            return false;
        }
        if (_tileColorArray == null || layer < 0 || layer >= _tileColorArray.GetLayers())
        {
            return false;
        }

        Image img = _tileColorArray.GetLayerData(layer);
        if (img == null)
        {
            WarnGroundTintUnavailable(layer, "could not read");
            return false;
        }
        // The imported atlas is VRAM-compressed; decode to RGBA before sampling.
        if (img.IsCompressed() && img.Decompress() != Error.Ok)
        {
            WarnGroundTintUnavailable(layer, "could not decompress");
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

    // Mean of one voxel_tiles_nrm_height layer's ALPHA (height) channel.
    //
    // Averaged in stored space with no sRGB decode, unlike the color atlas
    // above: the shader binds tile_nrm_height WITHOUT source_color, so the raw
    // stored value is what the cavity term compares against. Decoding here
    // would bias every mid low and reintroduce the per-tile dimming this exists
    // to cancel. Not alpha-weighted for the same reason — alpha IS the payload.
    //
    // Returns 0.5 (a no-op mid for a mean-centred term) if the layer can't be
    // decoded, so an unreadable atlas costs relief, never a brightness shift.
    private static float GetLayerHeightMid(TextureLayered nrmHeight, int layer)
    {
        const float NeutralMid = 0.5f;
        if (nrmHeight == null || layer < 0 || layer >= nrmHeight.GetLayers())
        {
            return NeutralMid;
        }
        Image img = nrmHeight.GetLayerData(layer);
        if (img == null || (img.IsCompressed() && img.Decompress() != Error.Ok))
        {
            GD.PushWarning($"ChunkMesh: could not read tile_nrm_height layer {layer} for cavity mid; using {NeutralMid}.");
            return NeutralMid;
        }

        int w = img.GetWidth();
        int h = img.GetHeight();
        if (w <= 0 || h <= 0)
        {
            return NeutralMid;
        }
        double sum = 0.0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                sum += img.GetPixel(x, y).A;
            }
        }
        return (float)(sum / (w * (double)h));
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
        // Drop the static debug-visual registration so evicted chunks don't
        // linger in it as freed instances.
        if (_ledgeBarrierVisual != null)
        {
            _barrierVisualChunks.Remove(this);
            _ledgeBarrierVisual = null;
        }

        // Pull this chunk's contributions out of the global detail-scatter
        // manager so they don't linger in the multimesh after eviction.
        // World may already be torn down on game shutdown — guard accordingly.
        if (_scatterPosted)
        {
            Sim.Current?.DetailScatter?.RemoveChunk(_scatteredChunkCoord);
            _scatterPosted = false;
        }
    }

    // Hands the detail-scatter posting for this coord over to the mesh that is
    // replacing this one in place. MUST be called before QueueFree on any
    // rebuild-in-place path: QueueFree runs _ExitTree at the END of the frame,
    // i.e. AFTER the replacement has already posted, so the RemoveChunk above
    // would delete whichever contributions are live and leave the chunk bare
    // until it next streams in.
    public void TransferDetailScatterTo(ChunkMesh replacement)
    {
        if (!_scatterPosted)
        {
            return;
        }
        _scatterPosted = false;
        if (replacement != null && !replacement._scatterPosted)
        {
            // The replacement skipped the scatter (its voxels didn't change), so
            // our instances stay in the multimesh untouched — it inherits the
            // duty of removing them when it evicts.
            replacement._scatteredChunkCoord = _scatteredChunkCoord;
            replacement._scatterPosted = true;
        }
    }

    // buildCollision / buildDetails default true for normal chunks. The
    // bird's-eye overlook loads its far backdrop ring with both false: the
    // player is movement-locked in the tree so nothing walks on those chunks
    // (no trimesh collision / water trigger needed), and at the zoomed-out
    // scale the sub-metre detail sprites are sub-pixel (skip the scatter to
    // save the multimesh rebuild + memory). They render mesh + prop scatter
    // only, and unload when the overview ends.
    // CUSTOM channel counts the two meshers fill — see BuildMesh for what each
    // terrain channel carries.
    private const int TERRAIN_CUSTOM_CHANNELS = 4;
    private const int WATER_CUSTOM_CHANNELS = 1;

    // Build and realize in one go — what a caller on the main thread with one
    // chunk to load wants (runtime streaming, the mesh rebuild queue). The
    // initial world fill instead calls the two halves separately so it can run
    // every BuildGeometry at once; see ChunkGeometry.
    public static ChunkMesh Create(
        ChunkState data,
        Func<int, int, int, int> getVoxel,
        Func<int, int, int, SharpAxes> getShape,
        Func<int, int, int, int> getTerrainId,
        Func<int, int, int, int> getOverlayId,
        Func<int, int, int, int> getSunlight,
        Func<int, int, int, bool> getSunOpaque,
        Func<int, int, int, bool> chunkExists,
        bool buildCollision = true,
        bool buildDetails = true,
        bool outOfLightWindow = false)
    {
        using var _prof = Profiler.Sample("ChunkMesh.Create");
        ChunkGeometry geo = BuildGeometry(data, getVoxel, getShape, getTerrainId, getOverlayId, getSunlight, getSunOpaque, chunkExists, buildCollision, buildDetails, outOfLightWindow);
        return Realize(geo);
    }

    // Invisible barriers at the top edge of every drop taller than a legal
    // step. Always built with terrain collision; whether anything collides with
    // them is a mask decision on the body (see ECollisionLayer.LedgeBarrier), so
    // toggling them costs nothing and needs no rebuild.
    //
    // The triangles themselves come from LedgeBarrierMesher during
    // BuildGeometry; this is only the physics-server half. `verts` is
    // CHUNK-LOCAL, like the terrain mesh — this node's Position is already the
    // chunk origin, so applying it again would place the barriers a whole chunk
    // away from the ground they guard.
    private void RealizeLedgeBarriers(Vector3[] verts)
    {
        var shape = new ConcavePolygonShape3D();
        shape.BackfaceCollision = true;
        shape.Data = verts;

        var body = new StaticBody3D();
        body.CollisionLayer = (uint)ECollisionLayer.LedgeBarrier;
        body.CollisionMask = 0;
        var col = new CollisionShape3D();
        col.Shape = shape;
        body.AddChild(col);
        AddChild(body);

        BuildLedgeBarrierVisual(verts);

        LedgeBarrierChunks++;
        LedgeBarrierFaces += verts.Length / 6;
        if (LedgeBarrierChunks == 1)
        {
            // One line, once per session, so a run's log shows whether ledge
            // barriers were generated at all. They are invisible and opt-in, so
            // silence is otherwise indistinguishable from them never running.
            GD.Print($"[ledge_barrier] first chunk generated {verts.Length / 6} faces");
        }
    }

    // Running totals across every chunk built this session. Barriers are
    // invisible, so these are the only way to confirm they were generated at
    // all, and to size their cost. Read with `ledge_barrier_stats`.
    public static int LedgeBarrierChunks;
    public static int LedgeBarrierFaces;

    // Visible stand-in for the barriers, off unless `ledge_barrier_debug` is on.
    // Invisible collision that silently lands in the wrong place looks exactly
    // like collision that does not exist — which cost two rounds of debugging
    // here — so being able to SEE where a barrier ended up is the difference
    // between a five-second answer and a guess.
    private MeshInstance3D _ledgeBarrierVisual;
    private static readonly List<ChunkMesh> _barrierVisualChunks = new();
    private static StandardMaterial3D _barrierDebugMaterial;

    private void BuildLedgeBarrierVisual(Vector3[] verts)
    {
        // Built in code rather than authored: it is a dev visualizer with
        // nothing a designer would tune, and giving it a .tres would mint a
        // resource UID this repo would then have to carry.
        if (_barrierDebugMaterial == null)
        {
            _barrierDebugMaterial = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = new Color(1f, 0.35f, 0.1f, 0.35f),
            };
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        _ledgeBarrierVisual = new MeshInstance3D();
        _ledgeBarrierVisual.Mesh = mesh;
        _ledgeBarrierVisual.MaterialOverride = _barrierDebugMaterial;
        _ledgeBarrierVisual.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _ledgeBarrierVisual.Visible = CVars.ledgeBarrierDebug.Value;
        AddChild(_ledgeBarrierVisual);
        _barrierVisualChunks.Add(this);
    }

    // Driven by the `ledge_barrier_debug` cvar's change callback, so toggling is
    // one pass over loaded chunks rather than a per-chunk _Process.
    public static void SetLedgeBarrierDebugVisible(bool visible)
    {
        for (int i = _barrierVisualChunks.Count - 1; i >= 0; i--)
        {
            ChunkMesh chunk = _barrierVisualChunks[i];
            if (chunk == null || !GodotObject.IsInstanceValid(chunk) || chunk._ledgeBarrierVisual == null)
            {
                _barrierVisualChunks.RemoveAt(i);
                continue;
            }
            chunk._ledgeBarrierVisual.Visible = visible;
        }
    }

    // The PURE half of a chunk build — no Node, no Resource, no rendering
    // server, no shared mutable state. Safe to run on a worker thread, which is
    // the whole reason it is separated out: it is ~98% of what a chunk costs.
    //
    // Everything it touches is either a local, the immutable ChunkState, or the
    // caller's read-only voxel accessors. The Profiler is deliberately NOT used
    // in here — its section stack is main-thread state (see Profiler.Sample), so
    // to profile the fill turn `chunk_parallel_fill` off and the whole build
    // runs through Create on the main thread again.
    public static ChunkGeometry BuildGeometry(
        ChunkState data,
        Func<int, int, int, int> getVoxel,
        Func<int, int, int, SharpAxes> getShape,
        Func<int, int, int, int> getTerrainId,
        Func<int, int, int, int> getOverlayId,
        Func<int, int, int, int> getSunlight,
        Func<int, int, int, bool> getSunOpaque,
        Func<int, int, int, bool> chunkExists,
        bool buildCollision,
        bool buildDetails,
        bool outOfLightWindow)
    {
        var geo = new ChunkGeometry
        {
            Data = data,
            ChunkCoord = data.ChunkCoord,
            BuildCollision = buildCollision,
            OutOfLightWindow = outOfLightWindow,
        };
        if (OnlyChunkFilter.HasValue && data.ChunkCoord != OnlyChunkFilter.Value)
        {
            return geo;
        }

        int chunkWorldX = data.ChunkCoord.X * ChunkState.SIZE;
        int chunkWorldY = data.ChunkCoord.Y * ChunkState.SIZE;
        int chunkWorldZ = data.ChunkCoord.Z * ChunkState.SIZE;

        // Terrain (Dual Contouring). Four RgbaFloat CUSTOM channels:
        //   CUSTOM0: (tile_a, tile_b, tile_c, blend_amplitude).
        //   CUSTOM1: (sharpness, kit_a, kit_b, kit_c). .x drives smooth-vs-flat
        //     shading; .yzw is the triangle's three corner kit ids (constant
        //     across the tri so the shader can barycentric-pick, same pattern as
        //     tile ids).
        //   CUSTOM2: (overlay_a, overlay_b, overlay_c, concavity). Per-corner
        //     authored overlay ids for the AUTO terrain branch.
        //   CUSTOM3: (openness, baked_sun, _, _). Per-vertex sun read from the
        //     air the surface faces — see ChunkMesherDC.BakeVertexSun. zw are
        //     free: the climbable-ledge mark used to live in .z and is now an
        //     overlay.
        var buf = new MeshBuffer(TERRAIN_CUSTOM_CHANNELS);

        // Hoisted: the ledge barriers are built further down and must stand on
        // the same surface the terrain mesh just made.
        ChunkMesherDC.Build(data, getVoxel, getShape, getTerrainId, getOverlayId, getSunlight, getSunOpaque, chunkExists, buf, chunkWorldX, chunkWorldY, chunkWorldZ, out bool hasAnyFace, out DcCellSurface dcSurface);
        geo.Terrain = buf;
        geo.HasTerrain = hasAnyFace;

        // Detail-sprite scatter (grass, flowers, etc.). Compute the per-entry
        // instance contributions and post them to the world-wide manager so
        // every chunk's instances of the same DetailEntry collapse into one
        // MultiMesh draw call. _ExitTree below removes this chunk's
        // contributions when the chunk evicts.
        if (buildDetails)
        {
            // Computed here, POSTED in Realize — the scatter manager owns
            // MultiMeshes, so handing it the contribution is a main-thread act.
            geo.WantsScatter = true;
            geo.Scatter = ChunkDetailScatter.Compute(data, getVoxel, _activeDetailGroups);
        }

        // Water (axis-aligned cubic faces)
        var bufWater = new MeshBuffer(WATER_CUSTOM_CHANNELS);

        WaterMesher.Build(data, getVoxel, bufWater, chunkWorldX, chunkWorldY, chunkWorldZ, out bool hasAnyWaterFace);
        geo.Water = bufWater;
        geo.HasWater = hasAnyWaterFace;

        if (buildCollision)
        {
            List<Vector3> tris = LedgeBarrierMesher.Build(getVoxel, dcSurface, chunkWorldX, chunkWorldY, chunkWorldZ);
            geo.LedgeBarrierTris = tris?.ToArray();
        }

        return geo;
    }

    // The MAIN-THREAD half: turn built geometry into nodes, meshes and
    // collision. Everything here touches the rendering server, the physics
    // server, the scene tree or shared statics, which is exactly what
    // BuildGeometry is kept clear of.
    public static ChunkMesh Realize(ChunkGeometry geo)
    {
        EnsureMaterialsInitialized();
        var chunk = new ChunkMesh();
        chunk.Position = new Vector3(
            geo.ChunkCoord.X * ChunkState.SIZE,
            geo.ChunkCoord.Y * ChunkState.SIZE,
            geo.ChunkCoord.Z * ChunkState.SIZE
        );
        chunk.RealizeGeometry(geo);
        return chunk;
    }

    private void RealizeGeometry(ChunkGeometry geo)
    {

        ChunkState data = geo.Data;
        bool buildCollision = geo.BuildCollision;
        bool outOfLightWindow = geo.OutOfLightWindow;

        if (geo.WantsScatter)
        {
            _scatteredChunkCoord = geo.ChunkCoord;
            Sim.Current?.DetailScatter?.SetChunk(geo.ChunkCoord, geo.Scatter);
            _scatterPosted = geo.Scatter != null;
        }

        if (geo.IsEmpty)
        {
            CollisionReady = true;
            return;
        }

        if (geo.HasTerrain)
        {
            // Normals are authored per-vertex by the mesher from the 8-corner
            // density gradient. Don't call GenerateNormals — run per-chunk it
            // would average only the owner chunk's triangles at boundary
            // vertices, producing normals that disagree with the neighbour's
            // and a visible slope-pick seam along chunk borders.
            ArrayMesh mesh;
            using (Profiler.Sample("ChunkMesh.Commit"))
            {
                mesh = geo.Terrain.ToArrayMesh(SharedMaterial);
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
            if (outOfLightWindow)
            {
                // Terrain normally shades from the live light volume, but the
                // volume is a player-centric toroidal window — a sample from
                // outside it wraps onto unrelated world. Backdrop chunks past
                // the window shade from the frozen per-vertex bake instead.
                visual.SetInstanceShaderParameter("use_baked_sun", 1f);
            }
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

        if (geo.LedgeBarrierTris != null)
        {
            RealizeLedgeBarriers(geo.LedgeBarrierTris);
        }

        HasWater = geo.HasWater;
        if (geo.HasWater)
        {
            ArrayMesh waterMesh;
            using (Profiler.Sample("ChunkMesh.WaterCommit"))
            {
                waterMesh = geo.Water.ToArrayMesh(WaterMaterial);
            }

            var waterVisual = new MeshInstance3D();
            waterVisual.Mesh = waterMesh;
            waterVisual.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

            // SHARED, like the backface below it and every other material here.
            // A per-chunk Duplicate() copies the parameter values as they stand
            // at build time and never sees another one: every runtime knob on
            // the water — the atlas tables, the cover tuning, water_cover_force
            // — reached the original and nothing on screen, which reads exactly
            // like the feature not working. Nothing sets a per-chunk water
            // parameter; the per-fragment ones are global uniforms.
            waterVisual.MaterialOverride = WaterMaterial;
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
