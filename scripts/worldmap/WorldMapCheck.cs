using Godot;
using System.Collections.Generic;
using System.Text;

// Loads a painted world-map document and reports what the bake would make of its
// WATER — how much stands, how much is latent under the ground, how much has
// been erased, and how many spill edges the map inks — then quits. It stops at
// the ink: a CASCADE is measured off baked voxels (WaterfallFinder) and this
// builds no world.
//
// Driven by the `worldmap_check` cvar off Main._Ready: the painter's fast
// self-quitting loop, the same shape as shader_check / block_check. It reads the
// layer images and nothing else — no world is built, no .hike is written — so it
// costs about a boot, where reaching the same answers through the painter means
// opening the UI and baking.
public static class WorldMapCheck
{
    // Every chunk-sized rect repainted on its own must reproduce what one
    // whole-map pass draws. That is the granularity the painter repaints at, and
    // the StampsIn prefilter is the thing most able to break it.
    private static int CompareStampRebuilds(WorldMapState ctx, int w, int h,
        int clipY, out int covered)
    {
        var under = new Color(0.5f, 0.5f, 0.5f);
        WorldMapState.StampPlan all = ctx.PlanStamps(new Rect2I(0, 0, w, h), clipY);
        covered = 0;
        int disagreements = 0;
        int step = ChunkState.SIZE;
        for (int rx = 0; rx < w; rx += step)
        {
            for (int rz = 0; rz < h; rz += step)
            {
                var rect = new Rect2I(rx, rz, Mathf.Min(step, w - rx), Mathf.Min(step, h - rz));
                WorldMapState.StampPlan local = ctx.PlanStamps(rect, clipY);
                for (int px = rect.Position.X; px < rect.Position.X + rect.Size.X; px++)
                {
                    for (int pz = rect.Position.Y; pz < rect.Position.Y + rect.Size.Y; pz++)
                    {
                        Color full = ctx.StampColorAt(all, px, pz, under, null);
                        if (full != under)
                        {
                            covered++;
                        }
                        if (ctx.StampColorAt(local, px, pz, under, null) != full)
                        {
                            disagreements++;
                        }
                    }
                }
            }
        }
        return disagreements;
    }

    // The hand-placed entities, by entry, and how many of each carry their OWN
    // customized entry rather than the palette's shared one. A fork that failed
    // to save reads as shared again here, which is the failure worth catching:
    // the world still bakes, it just bakes the palette's defaults.
    private static void ReportEntities(WorldMapState ctx, StringBuilder sb)
    {
        EntityPlacement[] entities = ctx.Placements?.entities ?? System.Array.Empty<EntityPlacement>();
        var byEntry = new Dictionary<string, (int Total, int Owned)>();
        foreach (EntityPlacement placement in entities)
        {
            if (placement?.entry == null)
            {
                continue;
            }
            bool owned = string.IsNullOrEmpty(placement.entry.ResourcePath);
            string name = owned
                ? (string.IsNullOrEmpty(placement.entry.ResourceName)
                    ? placement.entry.GetType().Name
                    : placement.entry.ResourceName)
                : StringExtensions.GetBaseName(StringExtensions.GetFile(placement.entry.ResourcePath));
            byEntry.TryGetValue(name, out (int Total, int Owned) count);
            byEntry[name] = (count.Total + 1, count.Owned + (owned ? 1 : 0));
        }
        var spread = new StringBuilder();
        foreach (KeyValuePair<string, (int Total, int Owned)> pair in byEntry)
        {
            spread.Append(spread.Length == 0 ? "" : ", ")
                .Append($"{pair.Key}:{pair.Value.Total}");
            if (pair.Value.Owned > 0)
            {
                spread.Append($" ({pair.Value.Owned} customized)");
            }
        }
        sb.AppendLine($"[worldmap_check] entities: {entities.Length} placed — "
            + (spread.Length == 0 ? "none" : spread.ToString()));

        // The fork the property panel makes must come back as the SAME entry
        // type. If Duplicate ever returns a bare Resource the panel silently
        // keeps editing the shared palette entry instead, and every signpost in
        // the world changes together — a failure with no error and no visible
        // tell until the bake.
        foreach (EntityPlacement placement in entities)
        {
            if (placement?.entry == null || string.IsNullOrEmpty(placement.entry.ResourcePath))
            {
                continue;
            }
            if (placement.entry.Duplicate(false) is not SpawnEntryData)
            {
                sb.AppendLine($"[worldmap_check] ERROR: {placement.entry.GetType().Name} does not survive "
                    + "Duplicate — per-placement entity properties cannot be edited");
            }
            break;
        }
    }

    public static void RunAndQuit(SceneTree tree, string path)
    {
        WorldMapData data = ResourceLoader.Load<WorldMapData>(path.Trim());
        if (data == null)
        {
            GD.PrintErr($"[worldmap_check] could not load '{path}' as a WorldMapData. "
                + "Usage `worldmap_check res://path/to/world_map.tres`");
            tree.Quit();
            return;
        }

        WorldMapState ctx;
        try
        {
            ctx = new WorldMapState(data);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[worldmap_check] could not open the document's layers: {e}");
            tree.Quit();
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[worldmap_check] {StringExtensions.GetFile(data.ResourcePath)} — "
            + $"{data.sizeChunksX}x{data.sizeChunksZ} chunks ({data.ImageWidth}x{data.ImageHeight} m), "
            + $"seaLevel {data.seaLevel}");

        int w = data.ImageWidth;
        int h = data.ImageHeight;
        int standing = 0;
        int latent = 0;
        int erased = 0;
        int edges = 0;
        // Spawn eligibility, counted by WHY a column was refused rather than as
        // one total: the interesting number after any change to CanSpawnAt is
        // which clause moved.
        int spawnable = 0;
        int wet = 0;
        int grade = 0;
        int edited = 0;
        // The voxel-edit layer, reported because a document that reads all-zero
        // here bakes with no tunnels and no built geometry and nothing else says
        // so until you are standing in it.
        int carved = 0;
        int added = 0;
        int addedAboveGround = 0;
        for (int px = 0; px < w; px++)
        {
            for (int pz = 0; pz < h; pz++)
            {
                if (ctx.Underwater(px, pz))
                {
                    standing++;
                }
                else if (ctx.HasWater(px, pz))
                {
                    latent++;
                }
                else
                {
                    erased++;
                }
                if (ctx.Underwater(px, pz))
                {
                    wet++;
                }
                else if (ctx.IsGradeAt(px, pz))
                {
                    grade++;
                }
                else if (ctx.SurfaceBelow(px, pz, int.MaxValue) != ctx.TerrainHeight(px, pz))
                {
                    edited++;
                }
                else if (ctx.CanSpawnAt(px, pz))
                {
                    spawnable++;
                }
                int th = ctx.TerrainHeight(px, pz);
                for (int wy = data.WorldMinY; wy <= data.WorldMaxY; wy++)
                {
                    byte edit = ctx.VoxelEdit(px, pz, wy);
                    if (edit == WorldMapState.EditCarve)
                    {
                        carved++;
                    }
                    else if (edit == WorldMapState.EditAdd)
                    {
                        added++;
                        if (wy > th)
                        {
                            addedAboveGround++;
                        }
                    }
                }
                for (int d = 0; d < 4; d++)
                {
                    int nx = px + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int nz = pz + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx >= 0 && nx < w && nz >= 0 && nz < h && ctx.SpillsOver(px, pz, nx, nz))
                    {
                        edges++;
                    }
                }
            }
        }
        // Latent is the resting state of a world nobody has erased water in, not
        // a warning: every dry column above the sea holds the prefill under it.
        sb.AppendLine($"[worldmap_check] water: {standing} columns standing, {latent} latent, "
            + $"{erased} erased (of {w * h})");

        // Anything left over was refused by the paving or placement clause.
        sb.AppendLine($"[worldmap_check] spawnable: {spawnable} columns "
            + $"(refused {wet} wet, {grade} grade, {edited} carved/built over, "
            + $"{w * h - spawnable - wet - grade - edited} paved/built)");

        sb.AppendLine($"[worldmap_check] voxel edits: {carved} carved, {added} added "
            + $"({addedAboveGround} of them above the height map)");

        // Edges the MAP inks, not cascades the world builds: a fall is measured
        // off the baked voxels (WaterfallFinder), which this check has none of.
        sb.AppendLine($"[worldmap_check] spill edges inked: {edges}");
        // Stamps are composited onto EVERY view now, fed by a per-rebuild
        // StampsIn() prefilter — so the invariant that matters is the one the
        // prefilter could break: a partial rebuild must reproduce a full one.
        // Compared here at the state level, chunk-sized rect by chunk-sized
        // rect, because that is the granularity the painter actually repaints
        // at and it needs no display buffer to check.
        WorldMapState.StampPlan allStamps = ctx.PlanStamps(new Rect2I(0, 0, w, h));
        int disagreements = CompareStampRebuilds(ctx, w, h, int.MaxValue, out int covered);
        sb.AppendLine($"[worldmap_check] stamps: {allStamps.Stamps.Length} placed, "
            + $"{covered} columns drawn, "
            + $"{disagreements} partial-vs-full disagreements (must be 0)");

        // Again on a CUTAWAY, which is the same invariant through the extra
        // `baseYs` array — parallel to the candidate list, so a prefilter that
        // returned a different set would pair a stamp with another's seat. The
        // clip is put one metre over the first stamp's base so it genuinely
        // straddles some geometry rather than trivially hiding everything.
        if (allStamps.Stamps.Length > 0)
        {
            int clip = ctx.StampBaseY(allStamps.Stamps[0]) + 1;
            int cutDisagreements = CompareStampRebuilds(ctx, w, h, clip, out int cutCovered);
            sb.AppendLine($"[worldmap_check] stamps cut at Y={clip}: {cutCovered} columns drawn, "
                + $"{cutDisagreements} partial-vs-full disagreements (must be 0)");
        }

        // The painted difficulty layer, which mobs read through
        // SpawnContext.MobLevelOverride and forges through ForgeLevelOverride.
        // Reported as the rounded tiers those two actually receive: a world that
        // reads all-zero here bakes every forge at the mildest tier and every mob
        // at its species base, which is invisible until you are standing there.
        var tiers = new int[16];
        int maxTier = 0;
        for (int px = 0; px < w; px++)
        {
            for (int pz = 0; pz < h; pz++)
            {
                int level = Mathf.Clamp(Mathf.RoundToInt(ctx.MobLevelAt(px, pz)), 0, tiers.Length - 1);
                tiers[level]++;
                maxTier = Mathf.Max(maxTier, level);
            }
        }
        var spread = new StringBuilder();
        for (int i = 0; i <= maxTier; i++)
        {
            spread.Append(i == 0 ? "" : ", ").Append($"L{i}:{tiers[i]}");
        }
        sb.AppendLine($"[worldmap_check] danger: {spread} columns");

        ReportEntities(ctx, sb);

        sb.Append("[worldmap_check] done");
        GD.Print(sb.ToString());
        tree.Quit();
    }
}
