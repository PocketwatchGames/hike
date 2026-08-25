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
    // The stroke was started with the RIGHT button. Not a modifier key, but it
    // reaches BeginStroke the same way and the press has to be able to tell:
    // alt+LMB aims the tool and alt+RMB aims the cutaway.
    Secondary = 8,
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

    // Apply one stamp at the given column texel. A tool writes its LAYER IMAGE
    // and nothing else — there is no live voxel world to update, and the bake
    // reads the images back at the end.
    void Paint(WorldMapState ctx, WorldMapBrush brush, Vector2I texel, bool erase);

    // Columns this tool is ABOUT to change, when they are not the brush disk the
    // host would otherwise assume. Consulted BEFORE Paint, so the undo snapshot
    // covers them: a tool that writes outside this is a tool whose edit cannot be
    // undone. Null means "the brush".
    Rect2I? TouchRect(WorldMapState ctx, Vector2I texel, bool erase);

    // Columns the last Paint changed, when they are NOT the brush disk the host
    // would otherwise assume — a stamp moves its whole footprint, which can sit
    // well outside the cursor. Null means "the brush", which is every tool that
    // paints a layer under its own ring.
    Rect2I? LastPaintRect { get; }

    // Cycle the primary parameter (e.g. brush op, region index, carve height).
    void Cycle(WorldMapState ctx, int dir);

    // Move the active elevation / cross-section (e.g. tunnel slice, ocean level).
    void AdjustLevel(WorldMapState ctx, int dir);

    // Where this tool wants the cutaway when it becomes active, or null to leave
    // it where it is. A tool that works under the ground wants the plane just
    // over the height it is about to paint at — otherwise picking it up means
    // finding the plane first, which on a fresh session is parked in the sky.
    int? CutawayFor(int headroom) => null;

    // The stamp this tool has selected, or null. Stamps are composited onto
    // EVERY view, so the painter needs to know which one to highlight without
    // caring which tool is active; only the scene tool answers with anything.
    SubscenePlacement SelectedPlacement => null;

    // The hand-placed ENTITY this tool has selected, or null. Drives the
    // property panel, which edits the selection's spawn entry — only the entity
    // tool answers with anything.
    EntityPlacement SelectedEntity => null;

    // The palette entry this tool would place next, or null. Placements of that
    // entry are picked out on the map, so "where are the chests" is answered by
    // choosing the chest in the palette rather than by clicking every mark.
    SpawnEntryData SelectedEntry(WorldMapState ctx) => null;

    // The hand-placed entity under this column, or null. The tool owns the hit
    // test — an entity is a point, so it is a proximity check with the tool's own
    // grab radius — which is what lets the painter grow and name whatever the
    // cursor is over without knowing how one is grabbed.
    EntityPlacement EntityUnder(WorldMapState ctx, Vector2I texel) => null;
}

// Which spawn layers a view previews as dots. A SET, not a choice: every view
// drawing ground shows the props, because props are what the ground is
// furnished with and nothing else on that map answers "is this spot already
// occupied". Mob dots stay with the layers that paint mobs — they are about
// encounters, not about terrain.
[System.Flags]
public enum ESpawnPreview
{
    None = 0,
    Props = 1,
    Mobs = 2,
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

    // Which spawn layers this view draws dots for. Views whose colour answers an
    // unrelated question (elevation, water, tunnels, region, zone, danger) draw
    // none — there the dots would be noise.
    ESpawnPreview PreviewLayer { get; }

    // Does this view cut the world away above WorldMapState.CutawayY? A column
    // solid at that level is rock the view has cut into and draws as such, and
    // every colour and outline below follows whatever is exposed under the cut.
    // False — every view but the voxel-edit one — means "the world seen from
    // above", and the painter passes int.MaxValue as the clip for those.
    bool CutsAway => false;
}
