using Godot;

// A painting tool. Each tool owns its parameters (radius, op, active elevation,
// cross-section, index...) and its visualization (View). The active tool drives
// both what a stroke does and how the 2D map is coloured.
public interface IWorldMapTool
{
    string Name { get; }

    // The tool's visualization, used to colour the 2D map while this tool is
    // active. One view class per tool (IWorldMapView).
    IWorldMapView View { get; }

    // Brush size in texels — a per-tool variable.
    float Radius { get; set; }

    // HUD: the tool's primary parameter (op / index / carve height).
    string StatusText(WorldMapState ctx);

    // HUD: the tool's active elevation / cross-section, or "" if it has none.
    string LevelText(WorldMapState ctx);

    // Apply one stamp at the given column texel. The tool stamps its layer and,
    // if it changed voxels, drives the live re-bake (ctx.Commit).
    void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase);

    // Cycle the primary parameter (e.g. brush op, region index, carve height).
    void Cycle(WorldMapState ctx, int dir);

    // Move the active elevation / cross-section (e.g. tunnel slice, ocean level).
    void AdjustLevel(WorldMapState ctx, int dir);
}

// A tool's visualization: the colour of column texel (px, pz) for the 2D map.
public interface IWorldMapView
{
    Color ColorAt(WorldMapState ctx, int px, int pz);
}
