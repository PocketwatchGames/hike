using Godot;

// A Sprite3D-derived authoring node that renders via WorldPropScatter at
// runtime and via the standard Sprite3D path in the editor. Drop one into
// a prop's .tscn in place of the old "Sprite3D + LitSprite" child; the
// rest of the scene (StaticBody3D collider, particles, lights, audio, an
// Area3D for grass) keeps working unchanged. Authoring stays per-scene
// so colliders match visuals and side-cars (anything that isn't a mesh)
// can still be attached.
//
// Editor: this script is NOT [Tool], so nothing custom runs in the editor —
// Sprite3D's built-in C++ preview renders the texture/region/offset
// straight from the .tscn, exactly as it would on a plain Sprite3D node.
// Adding [Tool] would let _Ready run in-editor, which trips up the
// Sprite3D mesh rebuild after a script-class swap (sprite appears clipped
// or stale). Authoring requires no script-side editor logic, so we just
// don't enable it.
//
// Runtime: in _Ready, the sprite snapshots its per-instance state
// (transform, terrain normal if AlignToTerrain > 0, atlas region/size),
// registers with Sim.Current.PropScatter, then hides itself
// (Visible=false on the Sprite3D mesh). The shared MultiMesh in
// WorldPropScatter draws all instances of the same (texture, forward
// offset, pass) bucket in one call. _ExitTree unregisters.
//
// Why subclass Sprite3D rather than wrap it: editor preview is free
// (Godot already draws Sprite3Ds), and authors edit familiar exports
// (Texture, RegionRect, FlipH, Offset). The runtime path bypasses the
// inherited drawing entirely by setting Visible=false.
[GlobalClass]
public partial class MultimeshPropSprite : Sprite3D
{
    // Per-instance uniform scale roll, baked once at _Ready and folded
    // into the registered transform's column lengths. (1.0, 1.0) disables.
    [Export] public float ScaleMin { get; set; } = 1.0f;
    [Export] public float ScaleMax { get; set; } = 1.0f;

    // Blend between upright billboarding (0) and terrain-aligned
    // billboarding (1). Same uniform sprite_lit / detail_sprite use.
    // When > 0, _Ready raycasts down to sample the terrain normal under
    // the sprite (mirrors Foliage.SampleTerrainNormal).
    [Export(PropertyHint.Range, "0,1,0.01")] public float AlignToTerrain { get; set; }

    // Push the rendered sprite toward the camera by this many meters along
    // the horizontal billboard-forward axis. This forks WorldPropScatter
    // buckets — distinct ForwardOffset values produce distinct multimeshes.
    // In practice authors use a small handful of values (0, ~1, ~1.3) so
    // the fork count stays low. Mirrors LitSprite.ForwardOffset.
    [Export] public float ForwardOffset { get; set; }

    // Index into MinimapFoliageColors palette. 0 = no minimap stamp.
    // Non-zero stamps the prop's authored color over the terrain pixel(s)
    // covering this prop's footprint. Conflict resolution between overlapping
    // props is by MinimapFoliageColors.priority — set on the palette entry,
    // not here.
    [Export] public byte MinimapFoliageId { get; set; } = 0;

    // Whether this sprite contributes a sun-aligned shadow caster pass.
    // True (default) = WorldPropScatter spawns this instance into the
    // shadow bucket alongside the visible bucket; false = visible only,
    // no shadow contribution.
    [Export] public bool CastsShadow { get; set; } = true;

    // Water reflection participation. Off / On / Auto mirrors
    // LitSprite.Reflects exactly. Auto opts in only when the sprite's
    // rendered world-space height clears AutoReflectMinHeight, so tiny
    // ground decor doesn't pay for a second pass that wouldn't read on
    // a rippled water surface anyway. Independent of whether water is
    // actually present below the sprite — the search at _Ready makes
    // the final decision; if no water is found, the sprite skips the
    // reflection bucket regardless of this setting.
    [Export] public LitSprite.ReflectMode Reflects { get; set; } = LitSprite.ReflectMode.Auto;
    [Export(PropertyHint.Range, "0,8,0.05")] public float AutoReflectMinHeight { get; set; } = 1.5f;
    // How far below the sprite feet to search for a water surface. 16
    // voxels handles a sprite standing on a pier over a deep lake; beyond
    // that the reflection is so dimmed by water depth tint it won't read.
    [Export(PropertyHint.Range, "0,64,1")] public int WaterReflectionSearchDepth { get; set; } = 16;

    // Resolved atlas texture (un-wrapping AtlasTexture if Texture is one),
    // exposed for WorldPropScatter to use as a bucket key. Computed once
    // at _Ready alongside the snapshot.
    public Texture2D AtlasTexture { get; private set; }

    // Per-instance state captured at _Ready and held on the sprite. Read
    // by WorldPropScatter.Rebuild on every dirty rebuild.
    public struct SnapshotData
    {
        // Translation = world position; column 0 length = world width;
        // column 1 length = world height. Texture-aspect baked in, then
        // scaled by per-instance scale. The shader recovers world W/H via
        // length(MODEL_MATRIX[0/1]) and divides by SpriteSize to get the
        // per-source-pixel step in world space.
        public Transform3D Transform;
        public Vector3 Normal;
        public float Align;
        public Vector2I RegionOrigin;
        public Vector2I SpriteSize;
        // Per-instance camera-relative shift, packed into INSTANCE_COLOR.b
        // by the bucket (per-instance, not a bucket key, so archetypes
        // sharing an atlas stay in one draw call).
        public float ForwardOffset;
        // Reflection-pass payload, baked at _Ready by a downward voxel
        // search. WaterY is the world Y of the water surface this sprite
        // mirrors across. LakeFloorY is the world Y of the first non-water
        // voxel below the surface — the reflection vertex shader clamps
        // its quad's lowest extent to this so the geometry stays inside
        // the visible water column and depth-test against lake-floor
        // terrain passes for the entire reflection (no occlusion holes,
        // no bleed over land). HasReflection gates whether the sprite
        // registers in the reflection bucket at all (false when no water
        // was found, when Reflects == Off, or when Auto's height
        // threshold rejects).
        public float WaterY;
        public float LakeFloorY;
        public bool HasReflection;
    }
    public SnapshotData Snapshot;

    private WorldPropScatter.Handle _visibleHandle;
    private WorldPropScatter.Handle _shadowHandle;
    private WorldPropScatter.Handle _reflectionHandle;
    private WorldPropScatter.Handle _blockLightShadowHandle;
    private WorldPropScatter _scatter;

    public override void _Ready()
    {
        // No editor-mode guard needed: this script isn't [Tool], so _Ready
        // only fires at runtime. (Defensive check kept off because in-editor
        // _Ready calls would catch us before WorldPropScatter exists.)
        _scatter = Sim.Current?.PropScatter;
        if (_scatter == null)
        {
            GD.PushWarning($"MultimeshPropSprite '{Name}' loaded with no Sim.Current.PropScatter — rendering as a regular Sprite3D fallback.");
            return;
        }

        ResolveAtlas(out Texture2D atlas, out Vector2I regionOrigin, out Vector2I spriteSize);
        AtlasTexture = atlas;

        float pixelSize = GetWorldPixelSize();
        // Per-instance scale roll. GD.RandRange returns a double; cast to
        // float since the snapshot is float-precision. Identical roll
        // distribution to LitSprite's per-instance scale roll, so visual
        // density of a field of these sprites matches the prior path.
        float scale = (float)GD.RandRange(ScaleMin, ScaleMax);
        float worldW = spriteSize.X * pixelSize * scale;
        float worldH = spriteSize.Y * pixelSize * scale;

        Vector3 normal = AlignToTerrain > 0f ? SampleTerrainNormal() : Vector3.Up;

        // Reflection eligibility = mode allows it AND water lives below.
        // Resolve both here so the snapshot carries everything the bucket
        // needs without the rebuild path needing access to WorldState.
        bool reflectsByMode = ShouldReflectByMode(spriteSize, pixelSize);
        float waterY = 0f;
        float lakeFloorY = 0f;
        bool hasReflection = false;
        if (reflectsByMode)
        {
            float? sampledWaterY = FindWaterSurfaceY(GlobalPosition);
            if (sampledWaterY.HasValue)
            {
                waterY = sampledWaterY.Value;
                lakeFloorY = FindLakeFloorY(GlobalPosition, waterY);
                hasReflection = true;
            }
        }

        // Build the per-instance transform with non-uniform basis
        // (column 0 length = worldW, column 1 length = worldH, column 2
        // = unit Z). Detail-scatter uses the same encoding so the shader
        // recovers world dimensions via the column lengths.
        var basis = new Basis(
            new Vector3(worldW, 0f, 0f),
            new Vector3(0f, worldH, 0f),
            new Vector3(0f, 0f, 1f));
        Snapshot = new SnapshotData
        {
            Transform = new Transform3D(basis, GlobalPosition),
            Normal = normal,
            Align = AlignToTerrain,
            RegionOrigin = regionOrigin,
            SpriteSize = spriteSize,
            ForwardOffset = ForwardOffset,
            WaterY = waterY,
            LakeFloorY = lakeFloorY,
            HasReflection = hasReflection,
        };

        var (vis, sh, refl, blockLight) = _scatter.Register(this);
        _visibleHandle = vis;
        _shadowHandle = sh;
        _reflectionHandle = refl;
        _blockLightShadowHandle = blockLight;

        // Hide the inherited Sprite3D mesh — the multimesh draws on our
        // behalf now. CastShadow.Off matches LitSprite's "visible never
        // casts" rule; the shadow bucket handles directional shadow.
        Visible = false;
        CastShadow = ShadowCastingSetting.Off;
    }

    public override void _ExitTree()
    {
        if (_scatter == null)
        {
            return;
        }
        if (_visibleHandle != null)
        {
            _scatter.Unregister(this, _visibleHandle);
            _visibleHandle = null;
        }
        if (_shadowHandle != null)
        {
            _scatter.Unregister(this, _shadowHandle);
            _shadowHandle = null;
        }
        if (_blockLightShadowHandle != null)
        {
            _scatter.Unregister(this, _blockLightShadowHandle);
            _blockLightShadowHandle = null;
        }
        if (_reflectionHandle != null)
        {
            _scatter.Unregister(this, _reflectionHandle);
            _reflectionHandle = null;
        }
    }

    // Reflection-mode resolution — mirrors LitSprite.ShouldReflect. Auto
    // opts in only when the sprite's world-space height clears
    // AutoReflectMinHeight; small ground decor would render a flipped
    // duplicate too small to read against ripples and isn't worth the
    // extra fragments. Doesn't consider water presence — that's the
    // FindWaterSurfaceY check downstream.
    private bool ShouldReflectByMode(Vector2I spriteSize, float pixelSize)
    {
        switch (Reflects)
        {
            case LitSprite.ReflectMode.Off: return false;
            case LitSprite.ReflectMode.On: return true;
            default:
                return spriteSize.Y * pixelSize >= AutoReflectMinHeight;
        }
    }

    // XZ search radius (in voxels) for the water surface lookup. The
    // sprite's own column is checked first; if no water there (shore-line
    // case — tree standing on dry ground next to a lake), we expand
    // outward in concentric square rings until we find a water column or
    // run out. Same per-body water surface lands at the same Y across the
    // whole pond, so the first hit's Y is the right reflection plane
    // regardless of which neighbor column it came from.
    private const int WATER_SEARCH_XZ_RADIUS = 4;

    // Returns the world Y of the nearest water surface for reflection
    // anchoring, or null if no water lives within WATER_SEARCH_XZ_RADIUS
    // voxels in XZ. Mirrors LitSprite.FindWaterSurfaceY's vertical
    // behaviour (handles both above-water and partially-submerged
    // sources) but extends the XZ search outward — required for static
    // props authored on the shore, whose column is solid ground. The
    // per-instance LitSprite path skips this because the player moves
    // and re-runs the search every frame; static props are baked once
    // and need to find adjacent water at _Ready time. Run once and
    // cached on the snapshot, so the cost is O(R²×depth) per prop
    // exactly once per spawn.
    private float? FindWaterSurfaceY(Vector3 world)
    {
        WorldState ws = Sim.Current?.WorldState;
        if (ws == null)
        {
            return null;
        }
        int cx = Mathf.FloorToInt(world.X);
        int cz = Mathf.FloorToInt(world.Z);
        int startY = Mathf.FloorToInt(world.Y);

        // Ring-expand outward: r=0 is just (cx, cz); r=1 is the 8 cells
        // around it; etc. Skip cells on the interior of each ring (only
        // the boundary contributes new cells), so each cell is checked
        // exactly once.
        for (int r = 0; r <= WATER_SEARCH_XZ_RADIUS; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    if (r > 0 && Mathf.Abs(dx) != r && Mathf.Abs(dz) != r)
                    {
                        continue;
                    }
                    float? y = FindWaterInColumn(ws, cx + dx, startY, cz + dz);
                    if (y.HasValue)
                    {
                        return y;
                    }
                }
            }
        }
        return null;
    }

    // Walks down from the water surface in the same column the sprite's
    // anchor sits over until it hits a non-water voxel; returns that
    // voxel's TOP face Y (= the floor of the water column). The shader
    // clamps the reflection's lowest vertex to this so the quad stays
    // inside the visible water slice and avoids depth-test occlusion by
    // the lake floor terrain. If the column never exits water within the
    // search depth (e.g. ocean floor is deeper than we care about),
    // falls back to the search-depth bound — the reflection will then
    // extend that far down and any deeper portion is occluded as before
    // (acceptable; deep water columns are dim anyway). World position
    // matches the sprite's actual anchor in case the XZ ring search
    // found water in a neighboring column instead of directly under.
    private float FindLakeFloorY(Vector3 sourceWorld, float waterY)
    {
        WorldState ws = Sim.Current?.WorldState;
        if (ws == null)
        {
            return waterY - WaterReflectionSearchDepth;
        }
        int wx = Mathf.FloorToInt(sourceWorld.X);
        int wz = Mathf.FloorToInt(sourceWorld.Z);
        int waterTopY = Mathf.FloorToInt(waterY);
        int minY = waterTopY - WaterReflectionSearchDepth;
        for (int y = waterTopY - 1; y >= minY; y--)
        {
            if (ws.GetVoxelWorld(wx, y, wz) != VoxelType.Water)
            {
                return y + 1;
            }
        }
        return minY;
    }

    // Vertical search within one XZ column. Inside-water case walks up
    // until the column exits water; outside-water case walks down until
    // it enters water. Returns the world Y of the surface (the air-voxel
    // floor that sits directly above the topmost water voxel). Null if
    // no water within WaterReflectionSearchDepth either way.
    private float? FindWaterInColumn(WorldState ws, int wx, int startY, int wz)
    {
        if (ws.GetVoxelWorld(wx, startY, wz) == VoxelType.Water)
        {
            int maxY = startY + WaterReflectionSearchDepth;
            for (int y = startY + 1; y <= maxY; y++)
            {
                if (ws.GetVoxelWorld(wx, y, wz) != VoxelType.Water)
                {
                    return y;
                }
            }
            return null;
        }

        int minY = startY - WaterReflectionSearchDepth;
        for (int y = startY - 1; y >= minY; y--)
        {
            if (ws.GetVoxelWorld(wx, y, wz) == VoxelType.Water)
            {
                return y + 1;
            }
        }
        return null;
    }

    // Unwrap AtlasTexture, otherwise read RegionEnabled / RegionRect from
    // Sprite3D's own properties. Same resolution rule a LitSprite uses, so
    // an existing prop scene's region settings carry over verbatim when
    // its Sprite3D node is replaced with a MultimeshPropSprite.
    private void ResolveAtlas(out Texture2D atlas, out Vector2I regionOrigin, out Vector2I spriteSize)
    {
        if (Texture is AtlasTexture at && at.Atlas != null)
        {
            atlas = at.Atlas;
            Rect2 r = RegionEnabled && RegionRect.Size.X > 0 ? RegionRect : at.Region;
            regionOrigin = new Vector2I((int)r.Position.X, (int)r.Position.Y);
            spriteSize = new Vector2I((int)r.Size.X, (int)r.Size.Y);
            return;
        }
        atlas = Texture;
        if (RegionEnabled && RegionRect.Size.X > 0 && RegionRect.Size.Y > 0)
        {
            regionOrigin = new Vector2I((int)RegionRect.Position.X, (int)RegionRect.Position.Y);
            spriteSize = new Vector2I((int)RegionRect.Size.X, (int)RegionRect.Size.Y);
            return;
        }
        if (Texture != null)
        {
            Vector2 ts = Texture.GetSize();
            regionOrigin = Vector2I.Zero;
            spriteSize = new Vector2I((int)ts.X, (int)ts.Y);
            return;
        }
        regionOrigin = Vector2I.Zero;
        spriteSize = new Vector2I(1, 1);
    }

    // Short downward raycast — same shape Foliage uses — so the sprite
    // leans with the actual visible slope (which the DC mesher may
    // interpolate slightly off the voxel grid). Falls back to world-up if
    // the ray misses (sprite floating over a carved hole, physics not yet
    // ready, etc.) so the sprite stays upright in degenerate cases.
    private Vector3 SampleTerrainNormal()
    {
        var space = GetWorld3D().DirectSpaceState;
        if (space == null)
        {
            return Vector3.Up;
        }
        Vector3 from = GlobalPosition + Vector3.Up * 0.1f;
        Vector3 to = GlobalPosition - Vector3.Up * 2.0f;
        using var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Environment);
        var result = space.IntersectRay(query);
        if (result.Count > 0 && result.TryGetValue("normal", out var normal))
        {
            return (Vector3)normal;
        }
        return Vector3.Up;
    }

    // World units per source pixel — the same global pixel-size factor
    // sprite_lit uses via the `sprite_chunky` shader global. Sourced from
    // project settings here so multimesh-prop world size stays in lockstep
    // with sprite_lit-rendered mobs / loot at the same pixel density.
    // Mirrors LitSprite.GetEditorPixelSize. Named GetWorldPixelSize (not
    // GetPixelSize) to avoid hiding the inherited SpriteBase3D.GetPixelSize().
    private static float GetWorldPixelSize()
    {
        Variant entry = ProjectSettings.GetSetting("shader_globals/sprite_chunky");
        if (entry.VariantType == Variant.Type.Dictionary)
        {
            var dict = entry.AsGodotDictionary();
            if (dict.TryGetValue("value", out Variant value))
            {
                return (float)value;
            }
        }
        return 1.0f;
    }
}
