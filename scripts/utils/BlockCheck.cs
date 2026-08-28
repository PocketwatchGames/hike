using Godot;
using System.Text;

// Loads the block catalog, runs its validator and dumps the resolved table,
// then quits. Driven by the `block_check` cvar off Main._Ready — the fast
// "does the block data still hold together" loop, the data-side twin of
// shader_check. Needs no world, no menu and no renderer.
public static class BlockCheck
{
    // The kit palette is data, and it is the .hike's WIRE FORMAT — every
    // TerrainId byte indexes it — so a change to it re-textures every world
    // already baked. Dumped here, beside the block table, because a diff of this
    // output is the cheapest proof that a palette edit only APPENDED.
    private static void DumpKitPalette(WorldGenData genData)
    {
        KitPalette palette = KitPalette.Build(genData?.kitPalette);
        var sb = new StringBuilder();
        sb.AppendLine($"[block_check] kit palette: {palette.Kits.Length} slots"
            + $", {palette.DetailGroups.Length} detail groups");
        for (int i = 0; i < palette.Kits.Length; i++)
        {
            TerrainKitData kit = palette.Kits[i];
            // The kit's own authored purpose, including the two nothing reads
            // yet. An UNSET one is a real authoring mistake — the passes gated
            // on Surface / Cave skip it silently — and printing only those two
            // hid it behind the Submerged and Shore kits, which also read "-".
            string purpose = kit == null ? "-" : kit.purpose.ToString().ToLowerInvariant();
            sb.AppendLine($"  {i,2}  {StringExtensions.GetFile(kit?.ResourcePath ?? "<null>"),-24} "
                + $"block={palette.BlockFor(i),-3} {purpose}");
        }
        GD.Print(sb.ToString().TrimEnd());
    }

    public static void RunAndQuit(SceneTree tree, WorldGenData genData = null)
    {
        DumpKitPalette(genData);
        BlockCatalog catalog = BlockCatalog.Active;
        if (catalog == null)
        {
            GD.PrintErr("[block_check] catalog failed to load.");
            tree.Quit();
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[block_check] {catalog.blocks?.Length ?? 0} blocks, air={catalog.AirBlockId}");
        sb.AppendLine("  id  name             top/side/bottom            climbGrowth         flags");
        for (int id = 0; id < BlockCatalog.MAX_BLOCKS; id++)
        {
            BlockData b = catalog.GetById(id);
            if (b == null)
            {
                continue;
            }
            string faces = b.IsInvisible()
                ? "-"
                : $"{Layer(b.top)}/{Layer(b.SurfaceFor(EBlockFace.Side))}/{Layer(b.SurfaceFor(EBlockFace.Bottom))}";
            var flags = new StringBuilder();
            flags.Append(b.solid ? "solid " : "");
            flags.Append(b.transparent ? "transparent " : "");
            flags.Append(b.cutawayIsWall ? "cutawayWall " : "");
            flags.Append(b.render == EBlockRender.Water ? "water " : "");
            flags.Append(b.lightAttenuation > 0 ? $"atten={b.lightAttenuation} " : "");
            flags.Append($"shape={b.defaultShape} band={b.wallBand.X:0.##}..{b.wallBand.Y:0.##}");
            // Resolved, not authored: shows the catalog default a block inherits
            // rather than the null it stores, which is what the shader uploads.
            string growth = Layer(catalog.ClimbGrowthFor(id));
            sb.AppendLine($"  {id,2}  {b.blockName,-16} {faces,-26} {growth,-19} {flags}");
        }
        // Overlay-only surfaces wear no block face, so the table above never
        // shows them — but they still claim an atlas layer and still feed the
        // shader's per-layer tables. Printed so a missing or unassigned one is
        // visible here rather than as a silently unrendered overlay.
        sb.Append("  overlays:");
        foreach (BlockSurfaceData surface in catalog.overlaySurfaces ?? System.Array.Empty<BlockSurfaceData>())
        {
            sb.Append($" {Layer(surface)}");
        }
        sb.AppendLine();
        GD.Print(sb.ToString());
        GD.Print("[block_check] done");
        tree.Quit();
    }

    private static string Layer(BlockSurfaceData surface)
    {
        return surface == null ? "-" : $"{surface.surfaceName}({surface.atlasBaseIndex})";
    }
}
