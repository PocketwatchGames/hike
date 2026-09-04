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
                        // Compared as the plan's ANSWER — which stamp, and the
                        // local Y it draws — rather than as the colour that
                        // answer inks to. Two stamps inking the same colour
                        // compare equal; two stamps do not.
                        //
                        // By PLACEMENT, never by the index: an index is into the
                        // plan that produced it, and a local plan holds only the
                        // stamps meeting its rect, so the same stamp has
                        // different indices in the two.
                        bool hit = ctx.StampHitAt(all, px, pz, out int i, out int top);
                        SubscenePlacement stamp = hit ? all.Stamps[i] : null;
                        if (hit)
                        {
                            covered++;
                        }
                        bool localHit = ctx.StampHitAt(local, px, pz,
                            out int li, out int localTop);
                        SubscenePlacement localStamp = localHit ? local.Stamps[li] : null;
                        if (localStamp != stamp || localTop != top)
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
            if (placement?.Entry == null)
            {
                continue;
            }
            bool owned = placement.IsCustomized;
            // Grouped by the palette ENTRY a placement came from, so the listing
            // counts "every npc" the way the palette's highlight picks them out
            // — a customized one included, since it is still one of them.
            string name = SpawnEntryData.PaletteName(placement.source);
            if (string.IsNullOrEmpty(name))
            {
                name = placement.Entry.GetType().Name;
            }
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

        ReportEntityLinks(sb, entities);
        ReportPalettes(sb, ctx);
        ReportPaletteEditors(sb, ctx);

        // The fork the property panel makes must come back as the SAME entry
        // type. If Duplicate ever returns a bare Resource the panel silently
        // keeps editing the shared palette entry instead, and every signpost in
        // the world changes together — a failure with no error and no visible
        // tell until the bake.
        foreach (EntityPlacement placement in entities)
        {
            if (placement?.source == null || placement.IsCustomized)
            {
                continue;
            }
            if (placement.source.Duplicate(false) is not SpawnEntryData)
            {
                sb.AppendLine($"[worldmap_check] ERROR: {placement.source.GetType().Name} does not survive "
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
        int walled = 0;
        int edited = 0;
        // The voxel-edit layer, reported because a document that reads all-zero
        // here bakes with no tunnels and no built geometry and nothing else says
        // so until you are standing in it.
        int carved = 0;
        int added = 0;
        int addedAboveGround = 0;
        // The two prop layers, counted as PAINTED columns against the props that
        // will actually stand. Placement is one per column, so the gap between
        // the two numbers is entirely what CanPlacePropAt refused — water, a
        // carve or a build over the surface, paving, a placement's footprint. A
        // wide gap is a painted region the bake will furnish only in patches,
        // which is a barrier with holes in it.
        int blockingPainted = 0;
        int blockingCovered = 0;
        int blockingStanding = 0;
        int blockingUncovered = 0;
        int blockingClearings = 0;
        int blockingNoFit = 0;
        int breakablePainted = 0;
        int breakableCovered = 0;
        int breakableStanding = 0;
        int pavedSurface = 0;
        int pavedUnder = 0;
        int pavedStranded = 0;
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
                else if (ctx.InBlockingRegion(px, pz))
                {
                    walled++;
                }
                else if (ctx.SurfaceBelow(px, pz, int.MaxValue) != ctx.TerrainHeight(px, pz))
                {
                    edited++;
                }
                else if (ctx.CanSpawnAt(px, pz))
                {
                    spawnable++;
                }
                if (ctx.PaintedCollidableAt(px, pz) != null)
                {
                    blockingPainted++;
                    if (!ctx.CollidableCoversAt(px, pz) && ctx.CanPlacePropAt(px, pz))
                    {
                        // A clearing the fill deliberately left behind the
                        // barrier, or a hole in the barrier itself.
                        if (ctx.CollidableInteriorAt(px, pz))
                        {
                            blockingClearings++;
                        }
                        else if (ctx.CollidableNoFitAt(px, pz))
                        {
                            blockingNoFit++;
                        }
                        else
                        {
                            blockingUncovered++;
                        }
                    }
                }
                if (ctx.CollidableCoversAt(px, pz))
                {
                    blockingCovered++;
                }
                if (ctx.CollidablePropAt(px, pz, out WorldMapState.PaintedProp _))
                {
                    blockingStanding++;
                }
                if (ctx.PaintedDestructibleAt(px, pz) != null)
                {
                    breakablePainted++;
                }
                if (ctx.DestructibleCoversAt(px, pz))
                {
                    breakableCovered++;
                }
                if (ctx.DestructiblePropAt(px, pz, out WorldMapState.PaintedProp _))
                {
                    breakableStanding++;
                }
                if (ctx.PavingAt(px, pz) != null)
                {
                    int pavedY = ctx.PavedYAt(px, pz);
                    if (ctx.PavingLevelAt(px, pz) == WorldMapState.PavedOnSurface)
                    {
                        pavedSurface++;
                    }
                    else if (ctx.SolidAt(px, pz, pavedY) && !ctx.SolidAt(px, pz, pavedY + 1))
                    {
                        pavedUnder++;
                    }
                    else
                    {
                        // The floor a road was laid on is no longer a floor, so
                        // the bake lays nothing: terrain repainted under it, or
                        // a carve took it. Only an absolute level can strand —
                        // a surface-seated one is re-resolved every bake.
                        pavedStranded++;
                    }
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
            + $"(refused {wet} wet, {grade} grade, {walled} inside a blocking region, "
            + $"{edited} carved/built over, "
            + $"{w * h - spawnable - wet - grade - walled - edited} paved/built)");

        // The standing count is exactly what the bake will place, and the covered
        // count is exactly what it will block: both read the same fill, which is
        // the whole point of resolving placement on the model.
        //
        // UNCOVERED must be 0. It counts painted columns the fill could have
        // filled and did not AND that are not deep enough inside the region to
        // be a deliberate clearing — i.e. a hole in the barrier itself, the one
        // thing this whole model exists to rule out. Clearings are reported
        // beside it because they are the saving: entities not spent on ground
        // behind the barrier.
        sb.AppendLine($"[worldmap_check] props: blocking {blockingPainted} columns painted, "
            + $"{blockingCovered} blocked by {blockingStanding} props, "
            + $"{blockingClearings} interior clearings, {blockingNoFit} too tight for the list, "
            + $"{blockingUncovered} uncovered (must be 0); "
            + $"breakable {breakablePainted} painted, {breakableCovered} blocked by {breakableStanding} props");

        sb.AppendLine($"[worldmap_check] paving: {pavedSurface} columns on the surface, "
            + $"{pavedUnder} on a floor under it, {pavedStranded} stranded (no floor at their level)");

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

            // The SLICE itself: a stamp's plan is the topmost solid voxel of the
            // scene AT OR BELOW the plane, so walking the plane down a building
            // must change what the plan draws. Reported as the plan's solid
            // columns and its own top at each metre of the first stamp — a
            // sequence that never moves means the cut is showing the roof
            // whatever the plane does, which is exactly what it used to do.
            // The TALLEST stamp, not the first: a one-storey slab slices
            // correctly and says nothing, while a building with rooms in it is
            // the case this exists for.
            int probeIndex = 0;
            for (int i = 1; i < allStamps.Stamps.Length; i++)
            {
                if (ctx.StampHeight(allStamps.Stamps[i]) > ctx.StampHeight(allStamps.Stamps[probeIndex]))
                {
                    probeIndex = i;
                }
            }
            SubscenePlacement probe = allStamps.Stamps[probeIndex];
            int baseY = ctx.StampBaseY(probe);
            var slice = new System.Text.StringBuilder();
            for (int y = ctx.StampHeight(probe) - 1; y >= 0; y--)
            {
                WorldMapState.StampPlan cut = ctx.PlanStamps(
                    new Rect2I(0, 0, w, h), baseY + y);
                // Columns with material right AT the plane against ones showing
                // something further down. A solid block answers "all at" at
                // every level; a building with rooms in it answers "walls at,
                // floor below", which is the case the slice exists for.
                int at = 0;
                int below = 0;
                foreach (int t in cut.Tops[probeIndex])
                {
                    if (t < 0) { continue; }
                    if (t == y) { at++; } else { below++; }
                }
                slice.Append($" y{y}:{at}at/{below}under");
            }
            sb.AppendLine($"[worldmap_check] stamp slice ({ctx.StampHeight(probe)}m tall, per level):{slice}");
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

        // Painted water types, reported because a document that reads all-zero
        // here bakes every body as whatever its ZONE says and nothing else
        // tells you — the same argument the paving and danger counts make.
        // Counted against the water layer, so a type painted where no water
        // stands shows up as stranded rather than silently doing nothing.
        var typeCounts = new System.Collections.Generic.Dictionary<int, int>();
        int typedDry = 0;
        int typedLatent = 0;
        for (int px = 0; px < w; px++)
        {
            for (int pz = 0; pz < h; pz++)
            {
                int idx = ctx.WaterTypeIndexAt(px, pz);
                if (idx < 0)
                {
                    continue;
                }
                if (!ctx.HasWater(px, pz))
                {
                    typedDry++;
                    continue;
                }
                // LATENT water — painted, but buried under ground that has not
                // been carved away — has no free surface for the bake to dress,
                // so its type stamps nothing until the land above it goes. Split
                // out because the totals otherwise disagree with the bake's
                // "painted" count by a factor of ten and nothing says why.
                if (!ctx.Underwater(px, pz))
                {
                    typedLatent++;
                    continue;
                }
                typeCounts.TryGetValue(idx, out int c);
                typeCounts[idx] = c + 1;
            }
        }
        if (typeCounts.Count == 0 && typedDry == 0 && typedLatent == 0)
        {
            sb.AppendLine("[worldmap_check] water types: none painted (every body takes its zone's)");
        }
        else
        {
            foreach (System.Collections.Generic.KeyValuePair<int, int> kv in typeCounts)
            {
                BlockData b = kv.Key < ctx.WaterTypes.Length ? ctx.WaterTypes[kv.Key] : null;
                bool ok = b != null && b.render == EBlockRender.Water;
                sb.AppendLine($"[worldmap_check] water type {kv.Key} {(b != null ? b.blockName.ToString() : "<empty palette slot>")}"
                    + $"{(ok ? "" : "  <-- NOT A WATER BLOCK, will not stamp")}: {kv.Value} columns");
            }
            if (typedLatent > 0)
            {
                sb.AppendLine($"[worldmap_check] water types on LATENT water (buried, stamps nothing until carved): {typedLatent}");
            }
            if (typedDry > 0)
            {
                sb.AppendLine($"[worldmap_check] water types on DRY columns (stranded, stamps nothing): {typedDry}");
            }
        }

        sb.Append("[worldmap_check] done");
        GD.Print(sb.ToString());
        tree.Quit();
    }

    // What each palette resolved to, and — for the INDEXED ones — the slot each
    // resource occupies.
    //
    // The slot is the point. A painted raster stores it, so this is the only
    // readout that can catch a ledger whose order has moved: a document whose
    // zone 4 stopped being the hub does not error, it just bakes a different
    // world. A DEAD slot (its file gone) is reported rather than skipped, since
    // the columns painted with it are still out there.
    private static void ReportPalettes(System.Text.StringBuilder sb, WorldMapState ctx)
    {
        foreach (WorldMapPaletteSource source in WorldMapPaletteSource.Table)
        {
            if (!source.Indexed)
            {
                sb.AppendLine($"[worldmap_check] palette {source.Id}: "
                    + $"{source.Discover().Length} found (free — no slots stored)");
                continue;
            }
            string[] slots = ctx.Palettes.For(source.Id).slots ?? System.Array.Empty<string>();
            var dead = new List<string>();
            for (int i = 0; i < slots.Length; i++)
            {
                if (!ResourceLoader.Exists(slots[i]))
                {
                    dead.Add($"{i}={slots[i].GetFile()}");
                }
            }
            sb.AppendLine($"[worldmap_check] palette {source.Id}: {slots.Length} slots"
                + (dead.Count > 0 ? $"  DEAD: {string.Join(", ", dead)}" : ""));
            for (int i = 0; i < slots.Length; i++)
            {
                sb.AppendLine($"[worldmap_check]   {i,3}  {slots[i].GetFile().GetBaseName()}");
            }
        }
    }

    // What the property panel actually lets an author set on a placement of each
    // palette entry — the answer to "can I give this NPC its own conversation?",
    // which otherwise takes opening the painter and clicking one.
    //
    // Uses the panel's OWN classifier rather than a second copy of the rules, so
    // a property that stops being editable is reported the day it stops. It
    // doubles as the check on ResourceTypeIndex: a picker row showing 0
    // candidates means the scan failed to see that type's files, which in the
    // panel looks exactly like "there are none authored".
    private static void ReportPaletteEditors(System.Text.StringBuilder sb, WorldMapState ctx)
    {
        // Per palette ENTRY, not per type: what a row may be set to is the
        // entry's own answer, so one line per class would report whichever entry
        // happened to come first and a broken variants list on any other would
        // be invisible.
        var seen = new HashSet<string>();
        foreach (SpawnEntryData entry in ctx.EntityPalette)
        {
            if (entry == null
                || !seen.Add($"{entry.GetType().Name}/{SpawnEntryData.PaletteName(entry)}"))
            {
                continue;
            }
            var editable = new List<string>();
            var picks = new List<string>();
            var locked = new List<string>();
            foreach (Godot.Collections.Dictionary property in
                WorldMapEntityInspector.OrderedProperties(entry))
            {
                var name = new StringName(property["name"].AsString());
                WorldMapEntityInspector.EPropertyEditor kind =
                    WorldMapEntityInspector.EditorFor(entry, name,
                        (Variant.Type)(long)property["type"],
                        (PropertyHint)(long)property["hint"],
                        out System.Type resourceType, out string[] names,
                        out Resource[] resources);
                if (kind == WorldMapEntityInspector.EPropertyEditor.ResourcePick)
                {
                    // A row constrained to the entry's own set reports its own
                    // count and is marked, so the listing distinguishes "every
                    // conversation in the project" from "this entry's 13 goblins".
                    picks.Add(resources != null
                        ? $"{name}({resources.Length} offered)"
                        : $"{name}({ResourceTypeIndex.Candidates(resourceType).Length})");
                }
                else if (kind == WorldMapEntityInspector.EPropertyEditor.NamePick)
                {
                    picks.Add($"{name}({names.Length})");
                }
                else if (kind == WorldMapEntityInspector.EPropertyEditor.ReadOnly)
                {
                    locked.Add(name.ToString());
                }
                else
                {
                    editable.Add(name.ToString());
                }
            }
            sb.AppendLine($"[worldmap_check] {SpawnEntryData.PaletteName(entry)} ({entry.GetType().Name}) panel — "
                + $"edit: {Join(editable)} | pick: {Join(picks)} | no editor yet: {Join(locked)}");
        }
    }

    private static string Join(List<string> items)
        => items.Count == 0 ? "none" : string.Join(", ", items);

    // Lever-to-trapdoor wiring, which is authored as a word typed twice and so
    // fails exactly the way an untyped identifier always does: silently. A lever
    // whose tag matches nothing throws its handle and opens no floor, and nothing
    // at runtime says so — the lever simply finds no trapdoor to trigger.
    //
    // A tagged trapdoor with no lever is reported too: it still opens by hand, so
    // it is not broken, but the tag is dead and usually means the lever's spelling
    // drifted.
    private static void ReportEntityLinks(System.Text.StringBuilder sb, EntityPlacement[] entities)
    {
        var levers = new Dictionary<string, int>();
        var trapdoors = new Dictionary<string, int>();
        int untargetedLevers = 0;
        int plainTrapdoors = 0;
        foreach (EntityPlacement placement in entities)
        {
            switch (placement?.Entry)
            {
                case LeverSpawnEntry lever:
                    if (string.IsNullOrEmpty(lever.targetLinkTag))
                    {
                        untargetedLevers++;
                        break;
                    }
                    levers.TryGetValue(lever.targetLinkTag, out int n);
                    levers[lever.targetLinkTag] = n + 1;
                    break;
                case TrapdoorSpawnEntry trapdoor:
                    if (string.IsNullOrEmpty(trapdoor.linkTag))
                    {
                        plainTrapdoors++;
                        break;
                    }
                    trapdoors.TryGetValue(trapdoor.linkTag, out int m);
                    trapdoors[trapdoor.linkTag] = m + 1;
                    break;
            }
        }
        if (levers.Count == 0 && trapdoors.Count == 0
            && untargetedLevers == 0 && plainTrapdoors == 0)
        {
            return;
        }
        var wiring = new List<string>();
        var dangling = new List<string>();
        var tags = new SortedSet<string>(levers.Keys);
        tags.UnionWith(trapdoors.Keys);
        foreach (string tag in tags)
        {
            levers.TryGetValue(tag, out int leverCount);
            trapdoors.TryGetValue(tag, out int trapdoorCount);
            wiring.Add($"{tag}: {leverCount} lever -> {trapdoorCount} trapdoor");
            if (trapdoorCount == 0)
            {
                dangling.Add($"{leverCount} lever(s) target '{tag}' and NO trapdoor carries it");
            }
            else if (leverCount == 0)
            {
                dangling.Add($"{trapdoorCount} trapdoor(s) carry '{tag}' and no lever targets it");
            }
        }
        sb.AppendLine($"[worldmap_check] entity links: {Join(wiring)}"
            + (plainTrapdoors == 0 ? "" : $" | {plainTrapdoors} player-operated trapdoor(s)"));
        if (untargetedLevers > 0)
        {
            sb.AppendLine($"[worldmap_check] ERROR: {untargetedLevers} lever(s) have no target link "
                + "tag and will NOT spawn — edit each placement and type the trapdoor's tag");
        }
        foreach (string line in dangling)
        {
            sb.AppendLine($"[worldmap_check] WARNING: {line}");
        }
    }
}
