using Godot;

// Coordinator for the minimap. Lives as a child of World, subscribes to
// chunk lifecycle events, drives data → texture updates and exploration
// reveal, and exposes the palette LUT textures + uniforms the HUD needs to
// render.
//
// One handler per signal:
//   ChunkManager.onChunkLoaded     → stamp surface (heightmap + detail-scatter
//                                    foliage) for that chunk.
//   World.onChunkEntitiesLoaded    → stamp prop foliage for that chunk
//                                    (entities aren't ready when the chunk
//                                    mesh first loads — props arrive later).
//
// The slice-atlas (indoor) pipeline isn't implemented yet — outdoor only.
// Mode toggle still picks a sensible default for the HUD shader so the
// surface texture renders correctly until indoor data lands.
[GlobalClass]
public partial class Minimap : Node3D
{
    public enum EMinimapMode
    {
        Outdoor,
        Indoor,
    }

    private World _world;
    private MinimapTextures _textures;
    private MinimapSliceAtlas _sliceAtlas;
    private MinimapFoliageColors _foliageColors;
    private Texture2D _tileLutTexture;
    private Texture2D _foliageLutTexture;
    // Reused buffers for chunk data generation — sized once at Initialize so
    // the per-chunk-load path allocates nothing.
    private MinimapData.SurfaceCell[] _surfaceCells;
    private MinimapData.SliceCell[] _sliceCells;

    private double _revealAccumulator;
    private Vector3 _lastRevealPos;
    private bool _hasRevealedOnce;

    private EMinimapMode _mode = EMinimapMode.Outdoor;
    private int _activeSliceLevel;

    // Smoothed reference elevation. Indoor target snaps at slice
    // boundaries; this lerp glides between the snap values so off-plateau
    // pixels don't reclassify all at once.
    private float _smoothedReferenceY;
    private bool _referenceInitialized;
    private const float ReferenceLerpRate = 6f;

    // Ping-pong render-state for crossfading mode toggles and slice level
    // crossings. Each frame the live ("B") snapshot reflects the current
    // mode + slice; when either changes, the previous B becomes A and the
    // transition fades from 0 (showing A) → 1 (showing B) over ~0.3s.
    public struct StateSnapshot
    {
        public Texture2D Surface;
        public Texture2D SurfaceBelow1;
        public Texture2D SurfaceBelow2;
        public Texture2D Exploration;
        public Texture2D ExplorationBelow1;
        public Texture2D ExplorationBelow2;
        public Vector2I WorldOriginXZ;
        public Vector2 ExtentPixels;
        public float MetersPerPixel;
        public float ReferenceElevation;
    }
    private StateSnapshot _stateA;
    private StateSnapshot _stateB;
    // 0 = render A; 1 = render B. Lerped to 1 each frame; reset to 0 on
    // state change.
    private float _stateTransition = 1f;
    private bool _stateInitialized;
    private EMinimapMode _lastCapturedMode;
    private int _lastCapturedSliceLevel;
    private const float StateLerpRate = 7f;

    // Reveal cadence. Reveal radius is vision × GameClient multiplier;
    // view radius (zoom) is computed by Hud from the TextureRect size +
    // GameClient.minimapPixelsPerMeter (decoupled from this Minimap).
    private const double RevealIntervalSeconds = 0.1;
    private const float RevealMoveThresholdSquared = 0.25f * 0.25f;
    // Fallback player vision range used when PlayerData isn't available
    // (typically only on the first frame before the player spawns).
    private const float DefaultVisionRange = 25f;

    public ImageTexture SurfaceTexture => _textures?.SurfaceTexture;
    public ImageTexture ExplorationTexture => _textures?.ExplorationTexture;

    public StateSnapshot StateA => _stateA;
    public StateSnapshot StateB => _stateB;
    public float StateTransition => _stateTransition;
    public Texture2D TileLutTexture => _tileLutTexture;
    public Texture2D FoliageLutTexture => _foliageLutTexture;
    public Vector2 ExtentMeters => _textures?.ExtentMeters ?? Vector2.Zero;
    public Vector2I WorldOriginXZ => _textures?.WorldOriginXZ ?? Vector2I.Zero;

    public EMinimapMode Mode => _mode;
    public int ActiveSliceLevel => _activeSliceLevel;
    public float ActiveMetersPerPixel => _mode == EMinimapMode.Outdoor
        ? MinimapData.OutdoorMetersPerPixel
        : MinimapData.IndoorMetersPerPixel;
    // Damped-lerped reference elevation. Outdoor target tracks the
    // player's eye height continuously (already smooth); indoor target
    // jumps at slice boundaries and the lerp glides between them so the
    // per-pixel above/below treatment doesn't pop.
    public float ActiveReferenceElevationY => _smoothedReferenceY;

    private float ComputeReferenceElevationTarget()
    {
        if (_mode == EMinimapMode.Outdoor)
        {
            float foot = _world?.player?.GlobalPosition.Y ?? 0f;
            return foot + GameCamera.EYE_HEIGHT;
        }
        return _activeSliceLevel * MinimapData.PlateauHeight + MinimapData.PlateauHeight * 0.5f;
    }

    // The texture and exploration map currently fronted by the HUD. The
    // shader sees one (surface_texture, exploration_texture) pair regardless
    // of mode — the coordinator picks the right pair each frame.
    public ImageTexture ActiveSurfaceTexture
    {
        get
        {
            if (_mode == EMinimapMode.Outdoor)
            {
                return _textures?.SurfaceTexture;
            }
            return _sliceAtlas?.TryGetLayer(_activeSliceLevel)?.TileTexture;
        }
    }
    public ImageTexture ActiveExplorationTexture
    {
        get
        {
            if (_mode == EMinimapMode.Outdoor)
            {
                return _textures?.ExplorationTexture;
            }
            return _sliceAtlas?.TryGetLayer(_activeSliceLevel)?.ExplorationTexture;
        }
    }

    // Underlying-slice textures for indoor display. The shader composites
    // active + below1 + below2 with brightness multipliers so the player
    // can see content at the slice below them when there's a hole in the
    // active slice. In outdoor mode these fall back to the active textures
    // — the composite then collapses to just the active layer (same content
    // sampled three times at 100/50/25% brightness; "current has content"
    // gating means the current always wins where it has data, and the
    // below layers contribute nothing new at empty pixels either).
    public ImageTexture ActiveSurfaceTextureBelow1
    {
        get
        {
            if (_mode == EMinimapMode.Outdoor || _sliceAtlas == null)
            {
                return ActiveSurfaceTexture;
            }
            return _sliceAtlas.TryGetLayer(_activeSliceLevel - 1)?.TileTexture
                ?? ActiveSurfaceTexture;
        }
    }
    public ImageTexture ActiveSurfaceTextureBelow2
    {
        get
        {
            if (_mode == EMinimapMode.Outdoor || _sliceAtlas == null)
            {
                return ActiveSurfaceTexture;
            }
            return _sliceAtlas.TryGetLayer(_activeSliceLevel - 2)?.TileTexture
                ?? ActiveSurfaceTextureBelow1;
        }
    }
    public ImageTexture ActiveExplorationTextureBelow1
    {
        get
        {
            if (_mode == EMinimapMode.Outdoor || _sliceAtlas == null)
            {
                return ActiveExplorationTexture;
            }
            return _sliceAtlas.TryGetLayer(_activeSliceLevel - 1)?.ExplorationTexture
                ?? ActiveExplorationTexture;
        }
    }
    public ImageTexture ActiveExplorationTextureBelow2
    {
        get
        {
            if (_mode == EMinimapMode.Outdoor || _sliceAtlas == null)
            {
                return ActiveExplorationTexture;
            }
            return _sliceAtlas.TryGetLayer(_activeSliceLevel - 2)?.ExplorationTexture
                ?? ActiveExplorationTextureBelow1;
        }
    }
    public Vector2I ActiveWorldOriginXZ
    {
        get
        {
            if (_mode == EMinimapMode.Outdoor)
            {
                return _textures?.WorldOriginXZ ?? Vector2I.Zero;
            }
            return _sliceAtlas?.WorldOriginXZ ?? Vector2I.Zero;
        }
    }
    public Vector2 ActiveExtentPixels
    {
        get
        {
            if (_mode == EMinimapMode.Outdoor)
            {
                if (_textures == null) { return Vector2.One; }
                return new Vector2(_textures.WidthPixels, _textures.HeightPixels);
            }
            if (_sliceAtlas == null) { return Vector2.One; }
            return new Vector2(_sliceAtlas.WidthPixels, _sliceAtlas.HeightPixels);
        }
    }

    public void Initialize(World world)
    {
        _world = world;
        GameClient gc = GameClient.Current;
        _foliageColors = gc?.minimapFoliageColors;
        Color wallSlotColor = gc != null ? gc.minimapWallSlotColor : new Color(0.045f, 0.045f, 0.05f);

        _textures = new MinimapTextures(world.WorldState);
        _sliceAtlas = new MinimapSliceAtlas(world.WorldState);
        _surfaceCells = new MinimapData.SurfaceCell[MinimapData.OutdoorPixelsPerChunkSq];
        _sliceCells = new MinimapData.SliceCell[MinimapData.IndoorPixelsPerChunkSq];

        _tileLutTexture = BuildTileLutTexture(BlockCatalog.Active, wallSlotColor);
        _foliageLutTexture = BuildFoliageLutTexture(_foliageColors);

        world.ChunkManager.onChunkLoaded += OnChunkLoaded;
        world.onChunkEntitiesLoaded += OnChunkEntitiesLoaded;

        // Catch up on chunks that loaded before we subscribed (typical when
        // Initialize runs after WorldGen has populated the world synchronously).
        foreach (var kv in world.WorldState._chunks)
        {
            OnChunkLoaded(kv.Key);
        }
    }

    public override void _ExitTree()
    {
        if (_world != null)
        {
            if (_world.ChunkManager != null)
            {
                _world.ChunkManager.onChunkLoaded -= OnChunkLoaded;
            }
            _world.onChunkEntitiesLoaded -= OnChunkEntitiesLoaded;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_world == null || _textures == null)
        {
            return;
        }
        Player player = _world.player;
        if (player == null)
        {
            return;
        }

        Vector3 playerPos = player.GlobalPosition;
        UpdateMode(playerPos);
        UpdateReferenceElevationLerp(delta);
        UpdateStateTransition(delta);

        _revealAccumulator += delta;
        bool moved = !_hasRevealedOnce
            || (playerPos - _lastRevealPos).LengthSquared() >= RevealMoveThresholdSquared;
        if (_revealAccumulator >= RevealIntervalSeconds && moved)
        {
            _revealAccumulator = 0.0;
            GameClient gc = GameClient.Current;
            float innerFraction = gc?.minimapRevealInnerFraction ?? 0.7f;
            // Reveal radius is independent of indoor zoom — it represents
            // what the player can perceive, which doesn't shrink just
            // because we're rendering a more zoomed-in indoor view.
            float revealRadius = ComputeRevealRadius();
            if (_mode == EMinimapMode.Outdoor)
            {
                _textures.RevealCircle(playerPos, revealRadius, innerFraction);
                RevealOutdoorSliceColumns(playerPos, innerFraction);
            }
            else
            {
                // Indoor / underground: reveal only the active slice.
                _sliceAtlas.RevealCircle(_activeSliceLevel, playerPos, revealRadius, innerFraction);
            }
            _lastRevealPos = playerPos;
            _hasRevealedOnce = true;
        }

        _textures.Flush();
        _sliceAtlas.Flush();
    }

    // Mirror the camera's cutaway state — the minimap and the camera should
    // never disagree about whether we're "indoors". This means the player's
    // Map button (CameraDown → ToggleClipAlways) toggles both at once. Slice
    // level always reflects the player's current Y so the indoor texture
    // matches whatever band they're standing in.
    private void UpdateMode(Vector3 playerPos)
    {
        GameCamera cam = _world.Camera;
        bool indoor = cam != null && cam.IsIndoorMode;
        _mode = indoor ? EMinimapMode.Indoor : EMinimapMode.Outdoor;
        int py = Mathf.FloorToInt(playerPos.Y);
        _activeSliceLevel = (int)Mathf.Floor((float)py / MinimapData.PlateauHeight);
    }

    // For each cell in a disk around the player, look up the ground height
    // from the outdoor heightmap and reveal that cell on the slice layer
    // whose Y range contains the ground voxel. Cells whose column has never
    // been stamped (height=0) are skipped — there's no ground to register
    // as visible. Cells whose target slice has no allocated layer no-op
    // inside the atlas (cheap dictionary miss).
    private void RevealOutdoorSliceColumns(Vector3 playerPos, float innerFraction)
    {
        if (_textures == null || _sliceAtlas == null)
        {
            return;
        }
        float radius = ComputeSliceRevealRadiusMeters(playerPos);
        if (radius <= 0f)
        {
            return;
        }
        float innerR = radius * Mathf.Clamp(innerFraction, 0f, 1f);
        float innerSq = innerR * innerR;
        float outerSq = radius * radius;
        int r = Mathf.CeilToInt(radius);
        int cx = Mathf.FloorToInt(playerPos.X);
        int cz = Mathf.FloorToInt(playerPos.Z);
        for (int dz = -r; dz <= r; dz++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                int distSq = dx * dx + dz * dz;
                if (distSq > outerSq)
                {
                    continue;
                }
                int wx = cx + dx;
                int wz = cz + dz;
                ushort height = _textures.GetHeightAtWorld(wx, wz);
                if (height == 0)
                {
                    continue;
                }
                int groundY = height - 1;
                int sliceLevel = (int)Mathf.Floor((float)groundY / MinimapData.PlateauHeight);
                byte target;
                if (distSq <= innerSq)
                {
                    target = 255;
                }
                else
                {
                    float t = (outerSq - distSq) / (outerSq - innerSq);
                    target = (byte)Mathf.Clamp((int)(t * 255f), 0, 255);
                }
                _sliceAtlas.RevealCellAtWorld(sliceLevel, wx, wz, target);
            }
        }
    }

    // Captures the active textures + per-state uniforms into the live
    // (B) snapshot every frame. When mode or slice level changes, copies
    // the previous B → A and resets _stateTransition to 0 so the shader
    // fades from old to new over ~0.3s.
    private void UpdateStateTransition(double delta)
    {
        StateSnapshot live = CaptureCurrentState();
        bool changed = !_stateInitialized
            || _mode != _lastCapturedMode
            || (_mode == EMinimapMode.Indoor && _activeSliceLevel != _lastCapturedSliceLevel);
        if (changed)
        {
            // First-ever capture skips the fade — start fully on B.
            _stateA = _stateInitialized ? _stateB : live;
            _stateB = live;
            _stateTransition = _stateInitialized ? 0f : 1f;
            _stateInitialized = true;
            _lastCapturedMode = _mode;
            _lastCapturedSliceLevel = _activeSliceLevel;
        }
        else
        {
            // Refresh B in case textures swap out from under us (e.g. a new
            // chunk loaded into the active slice layer).
            _stateB = live;
            float t = 1f - Mathf.Exp(-StateLerpRate * (float)delta);
            _stateTransition = Mathf.Lerp(_stateTransition, 1f, t);
        }
    }

    private StateSnapshot CaptureCurrentState()
    {
        Texture2D surf = ActiveSurfaceTexture;
        Texture2D expl = ActiveExplorationTexture;
        // Empty fallbacks: when no live texture exists yet (e.g. indoor mode
        // toggled into a slice that never loaded a layer), show void rather
        // than NaNs.
        return new StateSnapshot
        {
            Surface = surf,
            SurfaceBelow1 = ActiveSurfaceTextureBelow1 ?? surf,
            SurfaceBelow2 = ActiveSurfaceTextureBelow2 ?? surf,
            Exploration = expl,
            ExplorationBelow1 = ActiveExplorationTextureBelow1 ?? expl,
            ExplorationBelow2 = ActiveExplorationTextureBelow2 ?? expl,
            WorldOriginXZ = ActiveWorldOriginXZ,
            ExtentPixels = ActiveExtentPixels,
            MetersPerPixel = ActiveMetersPerPixel,
            ReferenceElevation = ActiveReferenceElevationY,
        };
    }

    // Reveal radius = vision × multiplier. Same value for outdoor and
    // indoor — the player perceives the same distance regardless of which
    // mode the minimap is rendering in. View radius (zoom) lives entirely
    // in Hud, computed from TextureRect size + GameClient pixels-per-meter.
    public float ComputeRevealRadius()
    {
        GameClient gc = GameClient.Current;
        float multiplier = gc?.minimapRevealMultiplier ?? 1.5f;
        float visionRange = _world?.player?.data?.visionRange ?? DefaultVisionRange;
        return visionRange * multiplier;
    }

    // Slice reveal radius = reveal radius scaled linearly by perceived
    // light at the player (ambient sky-light + block-light from torches).
    // Zero light = zero reveal: you can't chart what you can't see.
    private float ComputeSliceRevealRadiusMeters(Vector3 playerPos)
    {
        float baseRadius = ComputeRevealRadius();
        WorldState ws = _world?.WorldState;
        if (ws == null)
        {
            return baseRadius;
        }
        // GetPerceivedLightWorld returns max(sun, block); over-bright
        // possible from stacked block lights, so clamp to 1 for the
        // factor. light01 == 0 collapses the radius to 0.
        float perceived = ws.GetPerceivedLightWorld(playerPos, sunReachesPoint: true);
        float light01 = Mathf.Clamp(perceived, 0f, 1f);
        return baseRadius * light01;
    }

    // Glide the reference elevation toward its target. First call snaps so
    // the very first frame doesn't lerp from 0; subsequent calls damp toward
    // the target. Outdoor target moves continuously with the player so the
    // lerp is invisible there; indoor target jumps at slice boundaries and
    // the lerp visibly glides between them, preventing pop in the per-pixel
    // above/below classification.
    private void UpdateReferenceElevationLerp(double delta)
    {
        float target = ComputeReferenceElevationTarget();
        if (!_referenceInitialized)
        {
            _smoothedReferenceY = target;
            _referenceInitialized = true;
            return;
        }
        float t = 1f - Mathf.Exp(-ReferenceLerpRate * (float)delta);
        _smoothedReferenceY = Mathf.Lerp(_smoothedReferenceY, target, t);
    }

    private void OnChunkLoaded(Vector3I coord)
    {
        ChunkState chunk = _world.WorldState.GetChunk(coord);
        if (chunk == null)
        {
            return;
        }
        DetailGroupData[] detailPalette = ChunkMesh.ActiveDetailGroups;
        TerrainData[] terrainPalette = ChunkMesh.ActiveTerrains;
        MinimapData.GenerateSurfaceRow(chunk, detailPalette, terrainPalette, _surfaceCells);
        _textures.ApplyChunkSurface(coord, _surfaceCells, _foliageColors);

        // Slice tiles for every vertical slice this chunk overlaps. Empty
        // slices (from pure-air chunks, etc.) are skipped inside the atlas.
        // WorldState is passed so the top-slice "is the column above solid"
        // test can peek into the chunk above this one.
        _sliceAtlas.ApplyChunkSlices(coord, chunk, detailPalette, terrainPalette, _world.WorldState, _foliageColors, _sliceCells);

        // Try prop stamping in case entities are already loaded (e.g. catch-up
        // pass during Initialize where chunks pre-existed). Normal flow is
        // OnChunkEntitiesLoaded firing slightly later for entity-radius chunks.
        StampPropsForChunk(coord);
    }

    private void OnChunkEntitiesLoaded(Vector3I coord)
    {
        StampPropsForChunk(coord);
    }

    private void StampPropsForChunk(Vector3I coord)
    {
        if (_foliageColors == null)
        {
            return;
        }
        if (!_world.ActiveEntities.TryGetValue(coord, out var entities))
        {
            return;
        }
        foreach (Node3D entity in entities)
        {
            StampPropsRecursive(entity);
        }
    }

    private void StampPropsRecursive(Node node)
    {
        if (node is MultimeshPropSprite sprite && sprite.MinimapFoliageId != 0)
        {
            _textures.StampFoliagePoint(sprite.GlobalPosition, sprite.MinimapFoliageId, _foliageColors);
        }
        foreach (Node child in node.GetChildren())
        {
            StampPropsRecursive(child);
        }
    }

    // 64×1 LUT — 1px tall is enough; the shader samples with NEAREST on the
    // X axis to pick by tile id. Built once at Initialize; the catalog is
    // authored, not mutated at runtime.
    //
    // For each block in the catalog, every layer in its [base, base+LayerCount)
    // range is painted with the block's per-band color (variants within a
    // band share a color — the minimap doesn't differentiate them). Aliases
    // (a smaller block whose AtlasBaseIndex falls inside another block's
    // range, e.g. DesertSand@28 inside DesertTop@27..28) overwrite by virtue
    // of their position later in catalog.Blocks. WallSlotIndex paints the
    // authored wall color; unauthored slots stay magenta as a sanity-check.
    private static Texture2D BuildTileLutTexture(BlockCatalog catalog, Color wallSlotColor)
    {
        const int W = VoxelTypeInfo.TILE_VARIANT_TABLE_SIZE;
        Color[] table = new Color[W];
        Color unauthored = new Color(1f, 0f, 1f);
        for (int i = 0; i < W; i++)
        {
            table[i] = unauthored;
        }

        if (catalog?.Blocks != null)
        {
            foreach (BlockData block in catalog.Blocks)
            {
                if (block == null) { continue; }
                for (int band = 0; band < block.Bands; band++)
                {
                    Color c = block.GetMinimapColor(band);
                    int bandStart = block.AtlasBaseIndex + band * block.VariantsPerBand;
                    for (int v = 0; v < block.VariantsPerBand; v++)
                    {
                        int idx = bandStart + v;
                        if (idx >= 0 && idx < W)
                        {
                            table[idx] = c;
                        }
                    }
                }
            }
        }

        if (MinimapData.WallSlotIndex >= 0 && MinimapData.WallSlotIndex < W)
        {
            table[MinimapData.WallSlotIndex] = wallSlotColor;
        }

        byte[] pixels = new byte[W * 4];
        for (int i = 0; i < W; i++)
        {
            Color c = table[i];
            pixels[i * 4 + 0] = (byte)Mathf.Clamp((int)(c.R * 255f), 0, 255);
            pixels[i * 4 + 1] = (byte)Mathf.Clamp((int)(c.G * 255f), 0, 255);
            pixels[i * 4 + 2] = (byte)Mathf.Clamp((int)(c.B * 255f), 0, 255);
            pixels[i * 4 + 3] = (byte)Mathf.Clamp((int)(c.A * 255f), 0, 255);
        }
        Image img = Image.CreateFromData(W, 1, false, Image.Format.Rgba8, pixels);
        return ImageTexture.CreateFromImage(img);
    }

    // 256×1 LUT, same pattern. Foliage id 0 = "no stamp" so its slot is
    // never read; we still write a (transparent) entry to keep the array
    // contiguous.
    private static Texture2D BuildFoliageLutTexture(MinimapFoliageColors palette)
    {
        const int W = MinimapFoliageColors.Size;
        byte[] pixels = new byte[W * 4];
        for (int i = 0; i < W; i++)
        {
            MinimapFoliageEntry entry = palette?.Get(i);
            Color c = entry != null ? entry.Color : new Color(0f, 0f, 0f, 0f);
            pixels[i * 4 + 0] = (byte)Mathf.Clamp((int)(c.R * 255f), 0, 255);
            pixels[i * 4 + 1] = (byte)Mathf.Clamp((int)(c.G * 255f), 0, 255);
            pixels[i * 4 + 2] = (byte)Mathf.Clamp((int)(c.B * 255f), 0, 255);
            pixels[i * 4 + 3] = (byte)Mathf.Clamp((int)(c.A * 255f), 0, 255);
        }
        Image img = Image.CreateFromData(W, 1, false, Image.Format.Rgba8, pixels);
        return ImageTexture.CreateFromImage(img);
    }
}
