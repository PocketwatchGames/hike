using Godot;

// Claims one origin-centered world quadrant — the data-driven form of the
// legacy NE/NW/SE/SW split. Fixtures roll a random flat-dry column inside the
// quadrant (no fixed anchor).
[GlobalClass]
public partial class QuadrantBounds : ZoneBounds
{
    [Export] public EQuadrant quadrant;

    public override bool Contains(int chunkX, int chunkZ, in ZoneBoundsContext ctx)
    {
        bool east = chunkX >= 0;
        bool north = chunkZ >= 0;
        return quadrant switch
        {
            EQuadrant.NE => east && north,
            EQuadrant.NW => !east && north,
            EQuadrant.SE => east && !north,
            EQuadrant.SW => !east && !north,
            _ => false,
        };
    }
}
