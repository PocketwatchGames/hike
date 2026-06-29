using Godot;

// One authored road between two named points of interest. WorldGen pathfinds a
// route between the POIs (preferring flat/gentle ground, paying a steep cost to
// climb cliffs, and cheaply re-using earlier roads so a network branches rather
// than running parallel tracks), grades the terrain into a ramp where the route
// must climb a cliff, then paints the tread with this road's texture and width.
//
// Roads are processed in WorldGenData.Roads order; each one sees the columns
// laid by the roads before it as low-cost, so later connections merge onto and
// branch off the existing network.
[GlobalClass]
public partial class RoadConnection : Resource
{
    // POI names (from ZoneData.PointsOfInterest, resolved into
    // WorldState.PointsOfInterest). A connection whose endpoints don't both
    // resolve is skipped with a warning.
    [Export] public string FromPoi = "";
    [Export] public string ToPoi = "";

    // Tread width range in voxels. The road holds one width (rolled in
    // [MinWidth, MaxWidth]) for a random stride (WorldGenData.RoadStride*Meters),
    // then re-rolls — so a road swells and narrows along its length instead of
    // being a uniform ribbon. Keep MinWidth >= 2 so the mesher's neighborhood
    // overlay vote reliably reads the road texture rather than averaging it away.
    [Export(PropertyHint.Range, "1,16,1")] public int MinWidth = 3;
    [Export(PropertyHint.Range, "1,16,1")] public int MaxWidth = 4;

    // Overlay block stamped as the per-voxel OverlayId along the tread. Its
    // AtlasBaseIndex is the painted value — e.g. a "DirtOverlay" block for a
    // dirt road or a "Cobblestone" block for a stone road. Null falls back to
    // WorldGenData.RoadDefaultTexture.
    [Export] public BlockData Texture;
}
