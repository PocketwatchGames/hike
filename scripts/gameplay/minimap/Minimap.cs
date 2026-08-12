using System.Collections.Generic;
using Godot;

// Coordinator for the minimap. Lives as a child of Sim, subscribes to
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
[GlobalClass]
public partial class Minimap : Node3D
{
    public enum EMinimapMode
    {
        Outdoor,
        Indoor,
    }

    [ExportGroup("Style")]
    // Slice-view color for solid-rock columns. Painted at the reserved
    // MinimapData.WallSlotIndex slot in the tile LUT; kit-agnostic so a
    // tunnel through any biome reads as the same dark grey.
    [Export] public Color wallSlotColor = new Color(0.045f, 0.045f, 0.05f);
    // Color palette for foliage stamps on the minimap.
    [Export] public MinimapFoliageColors foliageColors;
    // Adaptive zoom: the minimap view radius (how much world the widget shows)
    // follows the player's current reveal distance (ComputeVisibleRevealRadiusMeters
    // — max reveal dimmed by time-of-day sun brightness + night vision, and scaled
    // by vision stats) times this margin, so the view sits just inside what's
    // charted. ~0.85 ≈ the old fixed zoom in daylight; 1 = flush with the edge.
    [Export(PropertyHint.Range, "0.3,1,0.01")] public float viewRevealMargin = 0.85f;
    // Floor for the adaptive view radius (meters) — the most the map ever zooms
    // IN (it zooms out farther when conditions are clear). This is the actual
    // on-screen minimap radius floor: the Hud floors the post-viewRevealMargin
    // view radius against it (not the pre-margin reveal distance). Set to the
    // world distance from screen center to a horizontal screen edge at the
    // standard orthographic camera on a 16:9 display, so at max zoom the minimap
    // disk reaches about as far as the player can see on screen. Camera ortho
    // Size (vertical world extent) = 20 → horizontal = 20·16/9 = 35.6 m → half ≈ 17.8.
    [Export(PropertyHint.Range, "2,64,0.1")] public float minViewRadiusMeters = 17.8f;
    // Extra indoor zoom-in factor — the view radius is divided by this in indoor
    // mode so corridors read closer. Presentation only; doesn't affect reveal.
    [Export(PropertyHint.Range, "1,8,0.25")] public float indoorZoom = 2f;
    // Reveal radius (what the player perceives) = vision × this. Drives both
    // the outdoor surface mask and the indoor active-slice mask; independent of
    // zoom because how far you see doesn't depend on how the map is rendered.
    [Export(PropertyHint.Range, "0.5,10,0.1")] public float revealMultiplier = 1.5f;
    // Extra reveal-radius multiplier while the player is in the bird's-eye
    // overlook (tree climb OR birds_eye consumable — they do the same thing) —
    // scouting from above charts farther than ground level, but a modest area,
    // not a huge swath. Stacks on revealMultiplier. The indoor light gate still
    // applies, so a dark region aloft stays uncharted.
    [Export(PropertyHint.Range, "1,8,0.1")] public float birdsEyeRevealMultiplier = 1.5f;
    // Soft-edge inner-fraction for every reveal disk. Inside `radius * this`
    // the disk paints at full brightness; from there to the outer radius the
    // value falls linearly to 0. 1.0 = hard edge, ~0.5 = wide soft fade.
    [Export(PropertyHint.Range, "0.1,1,0.05")] public float revealInnerFraction = 0.7f;

    [ExportGroup("Line of Sight")]
    // Master toggle. Off = the old plain filled-disk reveal (no occlusion, no fog).
    [Export] public bool losEnabled = true;
    // Sightline eye height above the player's feet for OUTDOOR reveal. The main
    // generosity knob — the higher the eye, the taller a rise must be before it
    // casts a map shadow, so small 2 m hillocks never shadow the valley behind
    // them. Indoor wall occlusion ignores this and uses the real camera eye.
    [Export(PropertyHint.Range, "0,20,0.5")] public float losEyeHeightMeters = 5f;
    // Vertical soft-shadow band in meters: how far below a ridge's horizon a
    // column fades from charted to hidden. Wider = softer terrain-shadow edges.
    [Export(PropertyHint.Range, "0.5,20,0.5")] public float losForgivenessMeters = 3f;
    // Base sample spacing along a sightline. Effective step grows with distance
    // so a ray never exceeds MinimapData.LosMaxStepsPerRay samples.
    [Export(PropertyHint.Range, "0.5,8,0.5")] public float losMarchStepMeters = 2f;
    // Meters of thickest (255) volumetric fog along a sightline that fully hides
    // the far end on the map. Painted fog volumes (swamp pools, etc.) between the
    // player and a distant area shorten how far the map charts through them; most
    // visible while scouting from the bird's-eye overlook. 0 = fog ignored.
    [Export(PropertyHint.Range, "0,64,1")] public float losFogFullBlockMeters = 16f;

    [ExportGroup("Campfire Reveal")]
    // Duration of the world-map reveal animation played when camping banks new
    // ground (GameClient.EnterCampWithFade → almanac). Newly charted cells grow in
    // as an animated noise threshold sweeps 0→1 over this many seconds.
    [Export(PropertyHint.Range, "0.25,5,0.05")] public float bankRevealSeconds = 1.5f;
    // Value-noise cell size in map pixels — larger = broader, softer reveal patches;
    // smaller = a finer, more granular dissolve.
    [Export(PropertyHint.Range, "2,64,1")] public float bankRevealNoiseCellPixels = 10f;
    // Soft-edge width of the sweeping threshold (in threshold units): cells fade in
    // across this band rather than snapping on as the threshold passes their noise value.
    [Export(PropertyHint.Range, "0.01,0.5,0.01")] public float bankRevealEdgeSoftness = 0.15f;

    private Sim _world;
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

    // Campfire reveal animation state. Baseline = the world-map (banked) outdoor
    // buffer as it was BEFORE the camp bank; _bankRevealTarget = the freshly-banked
    // buffer. _bankRevealCells lists the pixels that gained reveal, each paired with
    // a [0,1) noise threshold; the animation lerps a sweeping threshold up so a cell
    // fades from baseline to target as the sweep passes its noise value. Wall-clock
    // timed (presentational — a frozen sim clock must not stretch it).
    private byte[] _bankRevealBaseline;
    private byte[] _bankRevealTarget;
    private byte[] _bankRevealWork;
    private int[] _bankRevealCells;
    private float[] _bankRevealNoise;
    private bool _bankRevealPrepared;
    private bool _bankRevealAnimating;
    private ulong _bankRevealStartMs;

    // Live map markers, self-registered via World.onMapMarker{Spawned,Removed}.
    // Scanned each reveal tick for reveal-driven discovery (see UpdateMarkerDiscovery).
    private readonly HashSet<MapMarker> _markers = new();

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
        // Party-only (banked) exploration for the world map; the fields above are
        // party ∪ active for the minimap.
        public Texture2D ExplorationBanked;
        public Texture2D ExplorationBankedBelow1;
        public Texture2D ExplorationBankedBelow2;
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

    // Reveal cadence. Reveal radius is vision × revealMultiplier; the view radius
    // (adaptive zoom) follows ComputeVisibleRevealRadiusMeters (Hud reads it).
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

    // Top-face world Y at world XZ from the full-world outdoor heightmap (0 = no
    // content: off-map, or a column whose chunk hasn't stamped yet). A whole-world
    // CPU buffer, so it answers for columns that aren't currently streamed — used
    // to roll a treasure map's dig spot onto real charted land.
    public int SurfaceHeightAt(int wx, int wz) => _textures?.GetHeightAtWorld(wx, wz) ?? 0;

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

    // In BIASED height space, matching what the surface textures store — the
    // shader only ever compares this against sampled heights, so both sides must
    // use the same space. Anything handing a real world Y to the shader as a
    // reference elevation must bias it too (see WorldMapScreen's treasure map).
    private float ComputeReferenceElevationTarget()
    {
        if (_mode == EMinimapMode.Outdoor)
        {
            float foot = _world?.player?.GlobalPosition.Y ?? 0f;
            return foot + GameCamera.EYE_HEIGHT - HeightBias;
        }
        return _activeSliceLevel * MinimapData.PlateauHeight + MinimapData.PlateauHeight * 0.5f - HeightBias;
    }

    // Offset between real world Y and the heights stored in the map textures.
    public int HeightBias => _world?.WorldState != null ? MinimapData.HeightBias(_world.WorldState) : 0;

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
    // Banked (party-only) exploration — the world map samples these instead of
    // the party ∪ active textures above, so un-banked field reveal stays off it.
    public ImageTexture ActiveExplorationBankedTexture
    {
        get
        {
            if (_mode == EMinimapMode.Outdoor)
            {
                return _textures?.ExplorationBankedTexture;
            }
            return _sliceAtlas?.TryGetLayer(_activeSliceLevel)?.ExplorationBankedTexture;
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
    public ImageTexture ActiveExplorationBankedTextureBelow1
    {
        get
        {
            if (_mode == EMinimapMode.Outdoor || _sliceAtlas == null)
            {
                return ActiveExplorationBankedTexture;
            }
            return _sliceAtlas.TryGetLayer(_activeSliceLevel - 1)?.ExplorationBankedTexture
                ?? ActiveExplorationBankedTexture;
        }
    }
    public ImageTexture ActiveExplorationBankedTextureBelow2
    {
        get
        {
            if (_mode == EMinimapMode.Outdoor || _sliceAtlas == null)
            {
                return ActiveExplorationBankedTexture;
            }
            return _sliceAtlas.TryGetLayer(_activeSliceLevel - 2)?.ExplorationBankedTexture
                ?? ActiveExplorationBankedTextureBelow1;
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

    // The active member's provisional field-reveal buffers (the reveal target;
    // shown on the minimap but not the world map until banked) and the permanent
    // party pool's (banked reveal, shown on both). Null before the roster exists,
    // in which case reveal no-ops.
    private ExplorationMask ActiveExplorationMask =>
        _world?.WorldState?.SimState?.Party?.Active?.Knowledge?.Exploration;
    private ExplorationMask PartyExplorationMask =>
        _world?.WorldState?.SimState?.Party?.Knowledge?.Exploration;

    // Recompose the display exploration textures: the minimap texture from
    // party ∪ active (the controlled player's un-banked field reveal shows on
    // the minimap immediately), the world-map texture from the party pool only
    // (banked-at-a-campfire reveal). GameClient calls this on bank
    // (NotifyCampedAt) to fold freshly-banked reveal into the party pool, on
    // member switch (SyncControlToActive) to swap in the new active member's
    // provisional reveal, and on revive.
    public void RebuildExplorationDisplay()
    {
        ExplorationMask party = PartyExplorationMask;
        ExplorationMask active = ActiveExplorationMask;
        _textures?.RebuildExploration(party?.Outdoor, active?.Outdoor);
        _sliceAtlas?.RebuildExploration(party, active);
        // Reseeding the world-map fog from the party pool also retires the frozen
        // region/marker snapshots, so all three world-map layers revert together
        // and stay player-tied (lost on death, not stranded at party level).
        _world?.WorldState?.SimState?.ClearWorldMapSnapshots();
    }

    // Graduate the active member's field reveal onto the WORLD MAP as a frozen
    // snapshot — the tree-climb scout. Unlike a campfire bank, this doesn't touch
    // the party pool: it only advances the world map's banked-display buffers, so
    // the perched wide reveal shows on the world map immediately yet stays
    // provisional (un-banked) until the player actually returns to a fire. Normal
    // walking reveal never writes these buffers, so the world map holds this
    // snapshot frozen and only advances on the next tree climb.
    public void SnapshotFieldRevealToWorldMap()
    {
        ExplorationMask active = ActiveExplorationMask;
        if (active == null)
        {
            return;
        }
        _textures?.MergeActiveIntoBanked(active.Outdoor);
        _sliceAtlas?.MergeActiveIntoBanked(active);
    }

    // Campfire reveal animation. Armed by GameClient.NotifyCampedAt at camp entry,
    // but played later when the player opens the map:
    //   1. CaptureBankedRevealBaseline() — before the camp bank, snapshot the
    //      world-map buffer as-is.
    //   2. (bank happens: NotifyCampedAt → RebuildExplorationDisplay)
    //   3. PrepareBankedReveal() — diff the newly-banked buffer against the
    //      baseline; if new ground was charted, rewind the display to the baseline
    //      (so the map still shows the pre-camp state) and return true. The map is
    //      NOT updated on entering camp — it holds the baseline until step 4.
    //   4. StartBankedReveal() — fired when the player next opens the almanac to the
    //      world map (AlmanacScreen.ShowTab), in camp or later in the field.
    //   5. FinalizeBankedReveal() — on almanac close, snap to the fully-revealed map
    //      in case the player closed before the sweep finished. Idempotent.

    // True once a deferred reveal is armed (display rewound to the baseline) but
    // not yet played/finalized. Lets the camp bank avoid re-baselining across
    // successive camps the player hasn't yet opened the map to see.
    public bool BankRevealArmed => _bankRevealPrepared;

    public void CaptureBankedRevealBaseline()
    {
        _bankRevealBaseline = _textures?.CopyBankedOutdoor();
    }

    // Diff the freshly-banked buffer against the pre-bank baseline. If nothing new
    // was charted, returns false (caller skips the reveal and opens camp directly).
    // Otherwise rewinds the displayed world-map buffer to the baseline and arms the
    // animation (held at threshold 0 until StartBankedReveal).
    public bool PrepareBankedReveal()
    {
        ClearBankedReveal();
        byte[] baseline = _bankRevealBaseline;
        byte[] target = _textures?.CopyBankedOutdoor();
        if (baseline == null || target == null || baseline.Length != target.Length)
        {
            return false;
        }

        int changed = 0;
        for (int i = 0; i < target.Length; i++)
        {
            if (target[i] > baseline[i])
            {
                changed++;
            }
        }
        if (changed == 0)
        {
            return false;
        }

        _bankRevealTarget = target;
        _bankRevealWork = (byte[])baseline.Clone();
        _bankRevealCells = new int[changed];
        _bankRevealNoise = new float[changed];
        int width = _textures.WidthPixels;
        int c = 0;
        for (int i = 0; i < target.Length; i++)
        {
            if (target[i] <= baseline[i])
            {
                continue;
            }
            _bankRevealCells[c] = i;
            _bankRevealNoise[c] = RevealNoise(i % width, i / width);
            c++;
        }

        // Rewind the world map to the pre-camp state so the reveal grows from there.
        _textures.SetBankedOutdoor(baseline);
        _bankRevealPrepared = true;
        return true;
    }

    public void StartBankedReveal()
    {
        if (!_bankRevealPrepared)
        {
            return;
        }
        _bankRevealAnimating = true;
        _bankRevealStartMs = Time.GetTicksMsec();
    }

    // Snap to the fully-charted map and drop the animation state. Safe to call at
    // any point (never armed, mid-sweep, or already finished).
    public void FinalizeBankedReveal()
    {
        if (_bankRevealTarget != null)
        {
            _textures?.SetBankedOutdoor(_bankRevealTarget);
        }
        ClearBankedReveal();
    }

    // Alpha [0,1] for a world-map marker at worldXZ so its icon fades in with the
    // ground beneath it during the campfire reveal. Gated on _bankRevealPrepared
    // (not _bankRevealAnimating) so it tracks the rewound terrain the moment the map
    // is rewound — through the black/fade-in hold before the sweep starts — instead
    // of showing icons over still-hidden ground and then snapping them off. Returns
    // 1 with no reveal armed (and for markers on already-charted ground, whose
    // banked value is already full), so normal display and stable icons are untouched.
    public float BankedRevealAlphaAt(Vector3 worldXZ)
    {
        if (!_bankRevealPrepared)
        {
            return 1f;
        }
        return _textures?.SampleBankedOutdoorAlpha(worldXZ) ?? 1f;
    }

    private void ClearBankedReveal()
    {
        _bankRevealTarget = null;
        _bankRevealWork = null;
        _bankRevealCells = null;
        _bankRevealNoise = null;
        _bankRevealPrepared = false;
        _bankRevealAnimating = false;
    }

    // Advance the sweep one frame (called from _PhysicsProcess). The threshold ramps
    // 0→1 over bankRevealSeconds; each changed cell lerps baseline→target as the
    // threshold crosses its noise value, across a bankRevealEdgeSoftness-wide band.
    private void UpdateBankedReveal()
    {
        if (!_bankRevealAnimating)
        {
            return;
        }
        float elapsed = (Time.GetTicksMsec() - _bankRevealStartMs) / 1000f;
        float duration = Mathf.Max(bankRevealSeconds, 0.01f);
        float t = Mathf.Clamp(elapsed / duration, 0f, 1f);
        // Expand the sweep range slightly past [0,1] so the softness band fully
        // clears every cell (a cell at noise 1.0 still reaches full reveal at t=1).
        float soft = Mathf.Max(bankRevealEdgeSoftness, 0.0001f);
        float threshold = t * (1f + soft);

        for (int c = 0; c < _bankRevealCells.Length; c++)
        {
            int idx = _bankRevealCells[c];
            float reveal = Mathf.Clamp((threshold - _bankRevealNoise[c]) / soft, 0f, 1f);
            byte from = _bankRevealBaseline[idx];
            byte to = _bankRevealTarget[idx];
            _bankRevealWork[idx] = (byte)Mathf.RoundToInt(Mathf.Lerp(from, to, reveal));
        }
        _textures.SetBankedOutdoor(_bankRevealWork);

        if (t >= 1f)
        {
            FinalizeBankedReveal();
        }
    }

    // Coherent value noise in [0,1) over map-pixel coords — bilinearly interpolated
    // hashed lattice so reveal patches are contiguous blobs rather than TV static.
    private float RevealNoise(int px, int py)
    {
        float cell = Mathf.Max(bankRevealNoiseCellPixels, 1f);
        float fx = px / cell;
        float fy = py / cell;
        int x0 = Mathf.FloorToInt(fx);
        int y0 = Mathf.FloorToInt(fy);
        float tx = Smooth(fx - x0);
        float ty = Smooth(fy - y0);
        float n00 = Hash01(x0, y0);
        float n10 = Hash01(x0 + 1, y0);
        float n01 = Hash01(x0, y0 + 1);
        float n11 = Hash01(x0 + 1, y0 + 1);
        return Mathf.Lerp(Mathf.Lerp(n00, n10, tx), Mathf.Lerp(n01, n11, tx), ty);
    }

    private static float Smooth(float t)
    {
        return t * t * (3f - 2f * t);
    }

    private static float Hash01(int x, int y)
    {
        uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        h ^= h >> 13;
        h *= 2654435761u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    public void Initialize(Sim sim)
    {
        _world = sim;
        _foliageColors = foliageColors;

        _textures = new MinimapTextures(sim.WorldState);
        _sliceAtlas = new MinimapSliceAtlas(sim.WorldState);
        _surfaceCells = new MinimapData.SurfaceCell[MinimapData.OutdoorPixelsPerChunkSq];
        _sliceCells = new MinimapData.SliceCell[MinimapData.IndoorPixelsPerChunkSq];

        _tileLutTexture = BuildTileLutTexture(BlockCatalog.Active, wallSlotColor);
        _foliageLutTexture = BuildFoliageLutTexture(_foliageColors);

        sim.ChunkManager.onChunkLoaded += OnChunkLoaded;
        sim.onChunkEntitiesLoaded += OnChunkEntitiesLoaded;
        sim.onMapMarkerSpawned += OnMapMarkerSpawned;
        sim.onMapMarkerRemoved += OnMapMarkerRemoved;

        // Catch up on chunks that loaded before we subscribed (typical when
        // Initialize runs after WorldGen has populated the world synchronously).
        foreach (var kv in sim.WorldState._chunks)
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
            _world.onMapMarkerSpawned -= OnMapMarkerSpawned;
            _world.onMapMarkerRemoved -= OnMapMarkerRemoved;
        }
    }

    private void OnMapMarkerSpawned(MapMarker marker) => _markers.Add(marker);
    private void OnMapMarkerRemoved(MapMarker marker) => _markers.Remove(marker);

    public override void _PhysicsProcess(double delta)
    {
        using var _prof = Profiler.Sample("Minimap.PhysicsProcess");

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
        // Bird's-eye movement-locks the player, so the moved gate would fire
        // once on entry (with the ground-level radius already banked from
        // walking there) and then never again. Keep revealing while perched so
        // the wider birds-eye radius actually charts new ground — the max-merge
        // makes re-running on a stationary player essentially free.
        bool moved = !_hasRevealedOnce
            || (player.IsBirdsEye)
            || (playerPos - _lastRevealPos).LengthSquared() >= RevealMoveThresholdSquared;
        if (_revealAccumulator >= RevealIntervalSeconds)
        {
            _revealAccumulator = 0.0;
            if (moved)
            {
                RevealOnce(playerPos);
            }
            // Runs at the reveal cadence even when stationary, so a landmark that
            // streams in under already-charted fog is Sensed without waiting for
            // the player to move.
            UpdateMarkerDiscovery(playerPos);
        }

        // Drive the campfire reveal sweep (armed by GameClient during camp entry).
        UpdateBankedReveal();

        _textures.Flush();
        _sliceAtlas.Flush();
    }

    // One reveal pass at playerPos into the active member's field store (+ the live
    // display buffer). Shared by the per-tick reveal and the on-spawn RevealAtPlayerNow.
    private void RevealOnce(Vector3 playerPos)
    {
        float innerFraction = revealInnerFraction;
        // Reveal radius is independent of indoor zoom — it represents what the
        // player can perceive, which doesn't shrink just because we're rendering a
        // more zoomed-in indoor view.
        float revealRadius = ComputeRevealRadius();
        // The active member accumulates their own field reveal here; it stays off
        // the displayed world map until banked at a campfire. Null before the roster
        // exists — reveal then no-ops.
        ExplorationMask individual = ActiveExplorationMask;
        WorldState ws = _world.WorldState;
        MinimapLos los = BuildLos();
        if (_mode == EMinimapMode.Outdoor)
        {
            byte[] individualOutdoor = individual?.EnsureOutdoor(_textures.ExplorationBufferSize);
            bool birdsEye = _world.player?.IsBirdsEye ?? false;
            if (!los.Enabled)
            {
                _textures.RevealCircle(playerPos, revealRadius, innerFraction, individualOutdoor);
            }
            else if (birdsEye)
            {
                // Scouting from above: no terrain occlusion, but distant fog volumes
                // still hide what's inside them.
                _textures.RevealCircleFogged(playerPos, revealRadius, innerFraction, ws, los.FogFullBlockMeters, individualOutdoor);
            }
            else
            {
                _textures.RevealViewshed(playerPos, revealRadius, innerFraction, los, ws, individualOutdoor);
            }
            // Slice-column reveal gated by terrain LOS on the ground, but ungated in
            // bird's-eye (looking down over the terrain) and when LOS is off.
            RevealOutdoorSliceColumns(playerPos, innerFraction, individual, los, ws, gate: los.Enabled && !birdsEye);
        }
        else
        {
            // Indoor / underground: reveal only the active slice, with walls
            // occluding at the player's real eye height.
            float eyeY = playerPos.Y + GameCamera.EYE_HEIGHT;
            _sliceAtlas.RevealCircle(_activeSliceLevel, playerPos, revealRadius, innerFraction, individual, ws, eyeY, los);
        }
        _lastRevealPos = playerPos;
        _hasRevealedOnce = true;
    }

    // Force a single reveal pass at the player's current position right now, outside
    // the per-tick cadence. Called once at spawn (GameClient.Init) so the immediate
    // surroundings can be banked into the party pool — otherwise a fresh save opens
    // to a blank world map (the per-tick reveal only fills the active member's
    // provisional store, which the world map doesn't show until banked).
    public void RevealAtPlayerNow()
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
        RevealOnce(playerPos);
        UpdateMarkerDiscovery(playerPos);
        _textures.Flush();
        _sliceAtlas.Flush();
    }

    // Cheat (`reveal_map`): chart the whole world at once. Fills the active
    // member's field mask — the outdoor buffer plus every allocated slice layer —
    // names every region, senses every currently-loaded map marker, then runs the
    // normal campfire bank so all of it graduates into the party pool and shows on
    // the world map without a trip to a fire. Markers that haven't streamed in yet
    // aren't sensed; they discover on their usual path.
    public void RevealEverything()
    {
        if (_world == null || _textures == null)
        {
            return;
        }
        // An armed campfire sweep describes a map that no longer exists — drop it
        // first, or finalizing it later would overwrite the full reveal.
        ClearBankedReveal();

        ExplorationMask active = ActiveExplorationMask;
        if (active != null)
        {
            System.Array.Fill(active.EnsureOutdoor(_textures.ExplorationBufferSize), byte.MaxValue);
            _sliceAtlas.FillAllSlices(active);
        }
        WorldState ws = _world.WorldState;
        foreach (RegionData region in ws.RegionCentroidsXZ.Keys)
        {
            _world.DiscoverRegion(region);
        }
        Player player = _world.player;
        if (player != null)
        {
            UpdateMarkerDiscovery(player.GlobalPosition);
        }
        ws.SimState?.BankActiveKnowledge();
        RebuildExplorationDisplay();
        _textures.Flush();
        _sliceAtlas.Flush();
    }

    // Diagnostic (`minimap_probe`): print what the shader's height-derived terms
    // see, at the world map's zoom. `screenWidthPixels` is the on-screen width the
    // world map panel occupies, used to convert per-texel rise into the fwidth the
    // contour anti-aliasing reads.
    public string FormatHeightStats(float screenWidthPixels)
    {
        if (_textures == null)
        {
            return "minimap_probe: no active world.";
        }
        // Matches WorldMapScreen.RenderWorldMap's framing.
        Vector2 extent = ExtentMeters;
        float viewRadius = (extent.X + extent.Y) * 0.5f * Mathf.Sqrt2 * 0.5f;
        float metersPerScreenPixel = viewRadius * 2f / Mathf.Max(screenWidthPixels, 1f);
        return _textures.FormatHeightStats(ActiveReferenceElevationY, metersPerScreenPixel);
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
    private void RevealOutdoorSliceColumns(Vector3 playerPos, float innerFraction, ExplorationMask individual, in MinimapLos los, WorldState ws, bool gate)
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
        Vector2 eyeXZ = new Vector2(playerPos.X, playerPos.Z);
        float eyeY = playerPos.Y + los.EyeHeightMeters;
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
                int height = _textures.GetHeightAtWorld(wx, wz);
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
                if (gate)
                {
                    // A cliff face hidden behind a ridge shouldn't reveal its
                    // slice either — scale by the same terrain LOS the surface
                    // reveal uses (fog excluded; this is the secondary trace).
                    float vis = _textures.ColumnVisibility(eyeXZ, eyeY, wx, wz, los);
                    if (vis <= 0f)
                    {
                        continue;
                    }
                    target = (byte)Mathf.Clamp((int)(target * vis), 0, 255);
                }
                _sliceAtlas.RevealCellAtWorld(sliceLevel, wx, wz, target, individual);
            }
        }
    }

    private MinimapLos BuildLos()
    {
        return new MinimapLos(losEnabled, losEyeHeightMeters, losForgivenessMeters, losMarchStepMeters, losFogFullBlockMeters);
    }

    // Reveal-driven map-marker discovery, run each reveal tick. For every live
    // marker: mark it Sensed ("?" on the maps) once the outdoor fog has cleared
    // over its position, then — in Proximity mode — Identified once the player is
    // within identifyRadius. Perception / Interaction identify happen off-loop
    // (sibling Discoverable / host call). All writes go into the active member's
    // Knowledge, so the marker shows on the MINIMAP immediately (party ∪ active)
    // and graduates onto the WORLD MAP when banked at the next campfire. Gated on
    // the active member's OUTDOOR mask regardless of mode — markers are outdoor
    // landmarks and it's the persistent chart.
    private void UpdateMarkerDiscovery(Vector3 playerPos)
    {
        if (_markers.Count == 0)
        {
            return;
        }
        SimState sim = _world?.WorldState?.SimState;
        if (sim == null)
        {
            return;
        }
        byte[] outdoor = ActiveExplorationMask?.Outdoor;
        foreach (MapMarker marker in _markers)
        {
            Vector3 pos = marker.WorldPosition;
            EMapMarkerLevel known = sim.GetMarkerLevel(pos);
            if (known < EMapMarkerLevel.Sensed && _textures.IsRevealed(outdoor, pos))
            {
                sim.RecordMarker(pos, EMapMarkerLevel.Sensed, marker);
                known = EMapMarkerLevel.Sensed;
            }
            if (known == EMapMarkerLevel.Sensed
                && marker.IdentifyMode == EMapMarkerIdentifyMode.Proximity
                && marker.IdentifyRadius > 0f)
            {
                Vector3 flat = pos - playerPos;
                flat.Y = 0f;
                if (flat.LengthSquared() <= marker.IdentifyRadius * marker.IdentifyRadius)
                {
                    sim.RecordMarker(pos, EMapMarkerLevel.Identified, marker);
                }
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
        Texture2D explBanked = ActiveExplorationBankedTexture;
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
            ExplorationBanked = explBanked,
            ExplorationBankedBelow1 = ActiveExplorationBankedTextureBelow1 ?? explBanked,
            ExplorationBankedBelow2 = ActiveExplorationBankedTextureBelow2 ?? explBanked,
            WorldOriginXZ = ActiveWorldOriginXZ,
            ExtentPixels = ActiveExtentPixels,
            MetersPerPixel = ActiveMetersPerPixel,
            ReferenceElevation = ActiveReferenceElevationY,
        };
    }

    // Reveal radius = effective vision range × multiplier. Same value for outdoor
    // and indoor — the player perceives the same distance regardless of which mode
    // the minimap renders. Vision-affecting stats (base perception, buffs, gear —
    // EStat.Vision) scale it, so anything that extends the player's sight widens
    // both the charted map-reveal radius AND the adaptive zoom by the same factor.
    public float ComputeRevealRadius()
    {
        float multiplier = revealMultiplier;
        Player player = _world?.player;
        float visionRange = player?.data?.visionRange ?? DefaultVisionRange;
        if (player != null)
        {
            visionRange *= player.ComposeStat(EStat.Vision);
        }
        if (player?.IsBirdsEye ?? false)
        {
            multiplier *= birdsEyeRevealMultiplier;
        }
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
        // Night-vision gear lifts the light gate toward full brightness so the
        // player can still chart dark slices — same darkness relief the
        // perception system applies to sight (NightVision of 1.85 = 85% relief).
        Player player = _world?.player;
        if (player != null)
        {
            float nightVisionRelief = Mathf.Clamp(player.ComposeStat(EStat.NightVision) - 1f, 0f, 1f);
            if (nightVisionRelief > 0f)
            {
                light01 = Mathf.Lerp(light01, 1f, nightVisionRelief);
            }
        }
        return baseRadius * light01;
    }

    // The reveal distance the player can currently chart, for the adaptive minimap
    // zoom: the max reveal radius (already vision-stat scaled) dimmed by the global
    // time-of-day sun brightness and by the local painted fog at the player. NOT
    // floored here — the Hud applies viewRevealMargin and then floors the resulting
    // view radius at minViewRadiusMeters, so the floor bounds the true on-screen
    // radius rather than this pre-margin reveal distance.
    public float ComputeVisibleRevealRadiusMeters()
    {
        float radius = ComputeRevealRadius() * DaylightFactor01();
        // Local painted fog shortens how far it charts: in fog this thick the
        // sightline is limited to losFogFullBlockMeters (matches the reveal
        // viewshed's fog model, fog01 · d / F = 1). Fog is the ONE local sample
        // left; it only flickers at a fog-volume edge (swamp pools), and the Hud
        // zoom damp-lerp eases that crossing — unlike a per-frame local LIGHT
        // sample (canopy-dappled) which is why brightness now comes from the sky.
        WorldState ws = _world?.WorldState;
        Player player = _world?.player;
        if (ws != null && player != null && losFogFullBlockMeters > 0f)
        {
            Vector3 pos = player.GlobalPosition;
            int fx = Mathf.FloorToInt(pos.X);
            int fy = Mathf.FloorToInt(pos.Y + GameCamera.EYE_HEIGHT);
            int fz = Mathf.FloorToInt(pos.Z);
            float fog01 = ws.GetFogWorld(fx, fy, fz) / 255f;
            if (fog01 > 0f)
            {
                radius = Mathf.Min(radius, losFogFullBlockMeters / fog01);
            }
        }
        return radius;
    }

    // Global time-of-day sun/moon brightness in [0,1] — SkyController's blended
    // primary intensity (day-side ↔ night-side by NightT) normalized by the day
    // base. Unlike a locally-sampled light it doesn't flicker as the player walks
    // under canopy, so the zoom stays stable. Night-vision gear lifts it toward
    // full brightness (same darkness relief the perception system applies to
    // sight — NightVision 1.85 = 85% relief), so scouting at night with the gear
    // keeps the map zoomed out.
    private float DaylightFactor01()
    {
        SkyController sky = SkyController.Current;
        if (sky == null)
        {
            return 1f;
        }
        float dayBase = _world?.SimData?.dayIntensityBase ?? 2f;
        float sun01 = Mathf.Clamp(sky.CurrentPrimaryIntensity / Mathf.Max(dayBase, 0.001f), 0f, 1f);
        Player player = _world?.player;
        if (player != null)
        {
            float nightVisionRelief = Mathf.Clamp(player.ComposeStat(EStat.NightVision) - 1f, 0f, 1f);
            if (nightVisionRelief > 0f)
            {
                sun01 = Mathf.Lerp(sun01, 1f, nightVisionRelief);
            }
        }
        return sun01;
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
        MinimapData.GenerateSurfaceRow(chunk, detailPalette, terrainPalette, _surfaceCells, HeightBias);
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
        else if (node is MinimapFoliageStamp stamp && stamp.MinimapFoliageId != 0)
        {
            _textures.StampFoliagePoint(stamp.GlobalPosition, stamp.MinimapFoliageId, _foliageColors);
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
    // Each block is one atlas layer, painted at its AtlasBaseIndex with the
    // block's MinimapColor. WallSlotIndex paints the authored wall color;
    // unauthored slots stay magenta as a sanity-check.
    private static Texture2D BuildTileLutTexture(BlockCatalog catalog, Color wallSlotColor)
    {
        const int W = VoxelTypeInfo.MAX_ATLAS_LAYERS;
        Color[] table = new Color[W];
        Color unauthored = new Color(1f, 0f, 1f);
        for (int i = 0; i < W; i++)
        {
            table[i] = unauthored;
        }

        if (catalog?.blocks != null)
        {
            foreach (BlockData block in catalog.blocks)
            {
                if (block == null) { continue; }
                int idx = block.atlasBaseIndex;
                if (idx >= 0 && idx < W)
                {
                    table[idx] = block.minimapColor;
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
            Color c = entry != null ? entry.color : new Color(0f, 0f, 0f, 0f);
            pixels[i * 4 + 0] = (byte)Mathf.Clamp((int)(c.R * 255f), 0, 255);
            pixels[i * 4 + 1] = (byte)Mathf.Clamp((int)(c.G * 255f), 0, 255);
            pixels[i * 4 + 2] = (byte)Mathf.Clamp((int)(c.B * 255f), 0, 255);
            pixels[i * 4 + 3] = (byte)Mathf.Clamp((int)(c.A * 255f), 0, 255);
        }
        Image img = Image.CreateFromData(W, 1, false, Image.Format.Rgba8, pixels);
        return ImageTexture.CreateFromImage(img);
    }
}
