using Godot;
using System.Collections.Generic;
using System.Text;

// Loads a painted world-map document and reports what the bake would make of its
// WATER — how much stands, how much is latent under the ground, how much has
// been erased — plus every cascade it would file, then quits.
//
// Driven by the `worldmap_check` cvar off Main._Ready: the painter's fast
// self-quitting loop, the same shape as shader_check / block_check. It reads the
// layer images and nothing else — no world is built, no .hike is written — so it
// costs about a boot, where reaching the same answers through the painter means
// opening the UI and baking.
public static class WorldMapCheck
{
    private const int TOP_N = 8;

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
        int breached = 0;
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
                else if (ctx.IsTunnel(px, pz, ctx.TerrainHeight(px, pz)))
                {
                    breached++;
                }
                else if (ctx.CanSpawnAt(px, pz))
                {
                    spawnable++;
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
            + $"(refused {wet} wet, {grade} grade, {breached} tunnel-breached, "
            + $"{w * h - spawnable - wet - grade - breached} paved/built)");

        List<WaterfallSite> sites = ctx.BuildWaterfallSites();
        sb.AppendLine($"[worldmap_check] spill edges: {edges} -> {sites.Count} cascades");
        sites.Sort((a, b) => b.Height.CompareTo(a.Height));
        for (int i = 0; i < Mathf.Min(TOP_N, sites.Count); i++)
        {
            WaterfallSite s = sites[i];
            sb.AppendLine($"  ({s.Top.X:F0}, {s.Top.Y:F0}, {s.Top.Z:F0}) "
                + $"{s.Height}v tall, {s.Columns} col, {s.Lips.Count} lips");
        }
        if (sites.Count > TOP_N)
        {
            sb.AppendLine($"  ... {sites.Count - TOP_N} more");
        }
        sb.Append("[worldmap_check] done");
        GD.Print(sb.ToString());
        tree.Quit();
    }
}
