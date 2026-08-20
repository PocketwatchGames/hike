using Godot;

// Modifiers held when a stroke starts.
[System.Flags]
public enum EStrokeMods
{
    None = 0,
    // Alt: eyedropper. The press adopts what is under it as the tool's parameter
    // and paints nothing.
    Pick = 1,
    // Shift: paint only where the map EQUALS the column under the press — so a
    // stroke can work one terrace without touching the ones around it.
    Constrain = 2,
    // Ctrl: paint where the map is at or ABOVE the column under the press. The
    // one that scales: lifting a continent means "everything from the shoreline
    // up", across ground of every height, which equality cannot express.
    ConstrainAbove = 4,
}

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

    // Discrete choices for the tool's primary parameter — what Q/E cycles, and
    // what the HUD offers as a button group. Empty when the parameter is not a
    // small fixed set (a region index, a carve height), which is why Cycle stays
    // the general mechanism and this is only the presentable subset of it.
    string[] Options(WorldMapState ctx);

    // Swatch colour per option, or null for the theme default. Where the options
    // are things drawn on the map, this is what makes the toolbar the legend
    // instead of something to memorise.
    Color[] OptionColors(WorldMapState ctx);

    // Which of Options is selected. Setting it is equivalent to cycling to it.
    int OptionIndex { get; set; }

    // Colour of the brush ring. A tool that is about to write one specific value
    // shows it here, so the cursor answers "what am I about to paint" without a
    // trip to the HUD.
    Color CursorColor(WorldMapState ctx);

    // HUD: the modifiers this tool answers to, spelled out. A modifier nobody
    // can see is a modifier nobody uses.
    string HintText(WorldMapState ctx);

    // HUD: the tool's primary parameter (op / index / carve height).
    string StatusText(WorldMapState ctx);

    // HUD: the tool's active elevation / cross-section, or "" if it has none.
    string LevelText(WorldMapState ctx);

    // Mouse pressed, before the stroke paints anything, with the modifiers that
    // were held. A tool READS the map here: both modifiers are about the column
    // under the press, and both are decided once so the rest of the drag is
    // predictable.
    void BeginStroke(WorldMapState ctx, Vector2I texel, EStrokeMods mods);

    // Apply one stamp at the given column texel. The tool stamps its layer and,
    // if it changed voxels, drives the live re-bake (ctx.Commit).
    void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase);

    // Cycle the primary parameter (e.g. brush op, region index, carve height).
    void Cycle(WorldMapState ctx, int dir);

    // Move the active elevation / cross-section (e.g. tunnel slice, ocean level).
    void AdjustLevel(WorldMapState ctx, int dir);
}

// Which spawn layer a view previews as dots.
public enum ESpawnPreview
{
    None,
    Props,
    Mobs,
}

// A tool's visualization: the colour of column texel (px, pz) for the 2D map.
public interface IWorldMapView
{
    Color ColorAt(WorldMapState ctx, int px, int pz);

    // Draw outlines for sub-2m steps? Where the colour IS height (the elevation
    // bands), those lines say nothing the bands have not already said and add
    // noise to every gentle slope. Where the colour is a ground type or an
    // index, the outlines are the ONLY height information on screen and all of
    // them are wanted.
    bool ShowsAllSteps { get; }

    // Does this view composite standing water? If so its outlines follow the
    // water SURFACE, so the sea reads as one flat sheet outlined at its shore
    // rather than as a contour map of a seabed hidden under opaque water. Split
    // from ShowsAllSteps because a view can want every step AND blue water —
    // the two were only ever coincidentally the same set of views.
    bool DrawsWater { get; }

    // Which spawn layer, if any, this view draws dots for. Only the view whose
    // layer the spawns belong to wants them; elsewhere they would be noise over
    // an unrelated question.
    ESpawnPreview PreviewLayer { get; }

    // Ink the wall faces the climb pass would dress? Only the climb view wants
    // them: everywhere else they would claim the step outlines, which are the
    // one height cue the index views have.
    bool ShowsClimb { get; }
}
