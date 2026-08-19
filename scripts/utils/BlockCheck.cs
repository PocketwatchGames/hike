using Godot;
using System.Text;

// Loads the block catalog, runs its validator and dumps the resolved table,
// then quits. Driven by the `block_check` cvar off Main._Ready — the fast
// "does the block data still hold together" loop, the data-side twin of
// shader_check. Needs no world, no menu and no renderer.
public static class BlockCheck
{
    public static void RunAndQuit(SceneTree tree)
    {
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
