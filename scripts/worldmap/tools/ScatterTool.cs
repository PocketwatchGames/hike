using Godot;

// What a scatter cell places. Props (Tree/TallGrass) resolve from the column's
// zone surface kit; interactives (Loot/Chest/Torch) from the zone spawn lists —
// same resolution WorldEditor uses, so a painted world matches hand placement.
public enum EScatterKind
{
    Tree = 0,
    TallGrass = 1,
    Loot = 2,
    Chest = 3,
    Torch = 4,
}

// Density brush for props + interactives. Paints a per-column (kind, density)
// raster; the bake scatters one entity per column with probability == density,
// deterministically (hash-seeded), on dry land. Editing density re-scatters the
// affected columns live (into WorldState; the 3D preview re-syncs on Space).
public class ScatterTool : IWorldMapTool
{
    public string Name => "Scatter";
    public IWorldMapView View { get; }
    public float Radius { get; set; } = 16f;

    public EScatterKind Kind = EScatterKind.Tree;
    public float Density = 0.5f;

    public ScatterTool()
    {
        View = new ScatterView();
    }

    public string StatusText(WorldMapState ctx) => Kind.ToString();
    public string LevelText(WorldMapState ctx) => $"Density {Mathf.RoundToInt(Density * 100f)}%";

    public void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase)
    {
        byte kindId = (byte)((int)Kind + 1);
        Rect2I rect = brush.Stamp(texel, Radius, ctx.Data.ImageWidth, ctx.Data.ImageHeight, (px, pz, weight) =>
        {
            if (erase)
            {
                ctx.Scatter.SetPixel(px, pz, new Color(0f, 0f, 0f, 1f));
                return;
            }
            float existing = ctx.Scatter.GetPixel(px, pz).G;
            float d = Mathf.Max(existing, Density * weight);
            ctx.Scatter.SetPixel(px, pz, new Color(kindId / 255f, d, 0f, 1f));
        });
        if (rect.Size.X <= 0 || rect.Size.Y <= 0)
        {
            return;
        }
        ctx.RescatterColumns(rect);
    }

    public void Cycle(WorldMapState ctx, int dir)
    {
        int n = System.Enum.GetValues<EScatterKind>().Length;
        Kind = (EScatterKind)(((int)Kind + dir + n) % n);
    }

    public void AdjustLevel(WorldMapState ctx, int dir)
    {
        Density = Mathf.Clamp(Density + dir * 0.1f, 0f, 1f);
    }
}

// Dim terrain backdrop with scatter coverage tinted by kind + density, so you
// can see where things land relative to the land/water shape.
public class ScatterView : IWorldMapView
{
    public Color ColorAt(WorldMapState ctx, int px, int pz)
    {
        Color land = WorldMapState.Hypsometric(ctx.Elevation01(px, pz));
        Color bg = new Color(land.R * 0.45f, land.G * 0.45f, land.B * 0.45f);
        if (ctx.Underwater(px, pz))
        {
            bg = new Color(0.06f, 0.12f, 0.22f);
        }

        Color sc = ctx.Scatter.GetPixel(px, pz);
        int kindId = Mathf.RoundToInt(sc.R * 255f);
        float density = sc.G;
        if (kindId <= 0 || density <= 0f)
        {
            return bg;
        }
        return bg.Lerp(KindColor(kindId - 1), 0.3f + 0.7f * density);
    }

    private static Color KindColor(int kindIndex)
    {
        switch ((EScatterKind)kindIndex)
        {
            case EScatterKind.Tree: return new Color(0.15f, 0.6f, 0.2f);
            case EScatterKind.TallGrass: return new Color(0.55f, 0.8f, 0.3f);
            case EScatterKind.Loot: return new Color(0.95f, 0.85f, 0.25f);
            case EScatterKind.Chest: return new Color(0.85f, 0.55f, 0.2f);
            case EScatterKind.Torch: return new Color(0.95f, 0.45f, 0.15f);
            default: return Colors.Magenta;
        }
    }
}

// Resolves a scatter kind to an EntitySimState. Mirrors WorldEditor's entity
// resolution: props from the zone's SurfaceKit scene lists, interactives from
// the zone's spawn lists. Picks are hash-seeded so the bake is deterministic.
public static class ScatterFactory
{
    public static EntitySimState Create(EScatterKind kind, WorldMapData data, int zoneIdx, Vector3 pos, uint hash)
    {
        ZoneGenData[] zones = data.GenData?.Zones;
        ZoneGenData zone = (zones != null && zoneIdx >= 0 && zoneIdx < zones.Length) ? zones[zoneIdx] : null;
        TerrainKitData kit = zone?.SurfaceKit;

        switch (kind)
        {
            case EScatterKind.Tree:
            {
                WeightedList<PackedScene> w = WeightedScene.BuildList(kit?.TreeScenes);
                return w.Count > 0
                    ? new PropSimState(PropType.Tree, pos, w.Choose(HashF(hash, 1u) * w.TotalWeight))
                    : null;
            }
            case EScatterKind.TallGrass:
            {
                WeightedList<PackedScene> w = WeightedScene.BuildList(kit?.TallGrassScenes);
                return w.Count > 0
                    ? new PropSimState(PropType.Foliage, pos, w.Choose(HashF(hash, 2u) * w.TotalWeight))
                    : null;
            }
            case EScatterKind.Loot:
            {
                LootSpawnEntry e = FindFirst<LootSpawnEntry>(zone?.SurfaceEntities);
                if (e?.Item?.item == null)
                {
                    return null;
                }
                var sim = new LootSimState(pos, e.Item.item);
                if (e.Item.HasStatusEffects)
                {
                    sim.Item = e.Item.CreateState();
                }
                return sim;
            }
            case EScatterKind.Chest:
            {
                ChestSpawnEntry e = FindFirst<ChestSpawnEntry>(zone?.CaveEntities);
                return e?.Scene != null
                    ? new ChestSimState(pos, e.Scene) { LootItems = ChestSpawnEntry.Resolve(e.LootItems, new System.Random((int)hash)) }
                    : null;
            }
            case EScatterKind.Torch:
            {
                TorchSpawnEntry e = FindFirst<TorchSpawnEntry>(zone?.CaveEntities);
                return e?.Scene != null ? new TorchSimState(pos, e.Scene) : null;
            }
            default:
                return null;
        }
    }

    private static T FindFirst<T>(SpawnListData list) where T : SpawnEntryData
    {
        if (list?.Entries == null)
        {
            return null;
        }
        foreach (SpawnEntryData entry in list.Entries)
        {
            if (entry is T match)
            {
                return match;
            }
        }
        return null;
    }

    private static float HashF(uint h, uint salt)
    {
        unchecked
        {
            uint v = h ^ (salt * 0x9E3779B1u);
            v = ((v >> 16) ^ v) * 0x045D9F3Bu;
            v = (v >> 16) ^ v;
            return (v & 0xFFFFFFu) / 16777216f;
        }
    }
}
