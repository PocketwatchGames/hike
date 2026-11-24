using Godot;

// Authors trigger boxes (TriggerSpawnEntry): axis-aligned regions that fire
// actions and world-script hooks as actors walk into and out of them.
//
// The box gestures are BoxPlacementTool's. R/F changes a box's height, shift+R/F
// its depth below the floor. Everything else a trigger does is set in the
// property panel, which edits the box's own entry.
//
// Every box is its own entry (no palette), like the scene editor's roofs, so
// the placement holds only `custom`. Seated like an entity: on the floor the
// map shows under its CENTRE, which a lowered cutaway can make a passage.
public class TriggerTool : BoxPlacementTool
{
    public override string Name => "Triggers";

    // Height and depth a new box starts with, in metres.
    private const int NewHeightAbove = 4;
    private const int NewDepthBelow = 2;

    public override Color CursorColor(WorldMapInk ink) => ink.Data.triggerSelectedInk;

    public override string HintText(WorldMapState ctx)
        => "Drag on ground to draw a box  |  drag inside to move, on its border to resize "
            + "(Shift: always move)  |  R/F height, Shift+R/F depth  |  RMB delete  |  "
            + "name, actors and actions in the panel";

    public override string StatusText(WorldMapState ctx, WorldMapView view)
    {
        TriggerSpawnEntry t = WorldMapState.TriggerOf(Selected);
        if (t == null)
        {
            return $"{ctx.Placements.triggers.Length} trigger(s)";
        }
        return $"{Selected.Name ?? "unnamed"}: {t.sizeX}x{t.sizeZ} m, +{t.heightAbove}/-{t.depthBelow}, {t.actors}";
    }

    public override string LevelText(WorldMapState ctx, WorldMapView view)
    {
        if (Selected == null)
        {
            return "";
        }
        return Selected.floorY == EntityPlacement.OnTheGround ? "Seated on the ground" : $"Seated at y={Selected.floorY}";
    }

    protected override EntityPlacement BoxAt(WorldMapState ctx, Vector2I texel, int clipY, int grow)
        => ctx.TriggerAt(texel.X, texel.Y, clipY, grow);

    protected override Rect2I BoxTexelRect(WorldMapState ctx, EntityPlacement box) => ctx.TriggerTexelRect(box);

    protected override EntityPlacement CreateBox(WorldMapState ctx, WorldMapView view, Rect2I texelRect)
    {
        var placement = new EntityPlacement
        {
            custom = new TriggerSpawnEntry
            {
                heightAbove = NewHeightAbove,
                depthBelow = NewDepthBelow,
            },
        };
        ApplyRect(ctx, view, placement, texelRect);
        ctx.AddTrigger(placement);
        return placement;
    }

    protected override void RemoveBox(WorldMapState ctx, EntityPlacement box) => ctx.RemoveTrigger(box);

    // Write a texel rect onto a box and re-seat it under its new centre, so a
    // box dragged along a passage keeps that passage's floor and one dragged
    // out of the mouth goes back to the ground.
    protected override void ApplyRect(WorldMapState ctx, WorldMapView view, EntityPlacement placement, Rect2I texelRect)
    {
        var t = (TriggerSpawnEntry)placement.EditableEntry();
        placement.anchorXZ = ctx.WorldXZ(texelRect.Position);
        t.sizeX = texelRect.Size.X;
        t.sizeZ = texelRect.Size.Y;
        Vector2I seat = ctx.TexelXZ(t.SeatCell(placement.anchorXZ));
        placement.floorY = ctx.FloorForEntity(seat.X, seat.Y, view.CutawayY);
    }

    // R/F: the selected box's height above its floor.
    public override void AdjustLevel(WorldMapState ctx, int dir)
    {
        if (WorldMapState.TriggerOf(Selected) == null)
        {
            return;
        }
        var t = (TriggerSpawnEntry)Selected.EditableEntry();
        t.heightAbove = Mathf.Max(1, t.heightAbove + dir);
    }

    // Shift+R/F: how far it reaches below its floor.
    public override bool AdjustSecondaryLevel(WorldMapState ctx, int dir)
    {
        if (WorldMapState.TriggerOf(Selected) == null)
        {
            return true;
        }
        var t = (TriggerSpawnEntry)Selected.EditableEntry();
        t.depthBelow = Mathf.Max(0, t.depthBelow + dir);
        return true;
    }
}
