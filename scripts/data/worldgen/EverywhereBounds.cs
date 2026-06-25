using Godot;

// Claims every chunk — the world's background zone. Author at the lowest
// Priority so any inset (quadrant, box, circle) overrides it where they apply.
[GlobalClass]
public partial class EverywhereBounds : ZoneBounds
{
    public override bool Contains(int chunkX, int chunkZ, in ZoneBoundsContext ctx)
    {
        return true;
    }
}
