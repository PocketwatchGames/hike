using Godot;

// What the author is currently LOOKING AT and WORKING AT: the cutaway plane and
// whether water is drawn. One per painter session; nothing here is saved.
//
// Neither the document's nor the renderer's. It lived on WorldMapState, where
// the bake, undo, resize and worldmap_check could all see it and all had to
// ignore it — and WorldMapState never read either field, which is the tell that
// it was only parked there. It cannot move to WorldMapInk either: the cutaway
// plane is an input to PAINTING (a carve, a paving level, an entity's seat), and
// a mutation must never take a renderer — that would make the write path depend
// on the draw path and put the palette back within reach of an edit.
//
// So the tools take it alongside the map, and only on the four operations that
// genuinely depend on the working plane. Model queries never read it: they take
// a clipY parameter instead, which is what lets one view cut and another not.
public class WorldMapView
{
    private readonly WorldMapData _data;

    // The plane the cutting views draw at, in world voxel Y, and the level the
    // tools that work under the ground write at.
    //
    // An INSPECTION control as much as a tool parameter — sweeping it through a
    // corridor is how a 2D map answers "how tall is this" — and SHARED, so
    // switching between the tunnel and block tools keeps the same slice on
    // screen.
    public int CutawayY;

    // Whether the views composite standing water. Off shows the bare banded
    // height field, which is what you want while shaping a lake bed or a coast
    // you have already flooded.
    public bool ShowWater = true;

    // Where the cutaway starts a session: the top of the world, i.e. NOT CUT.
    // Every view that cuts is then exactly what it would be without one until
    // the plane is lowered, which is what lets the water tool share the
    // mechanism without its ordinary surface map opening full of rock.
    public WorldMapView(WorldMapData data)
    {
        _data = data;
        CutawayY = data.WorldMaxY;
    }

    // Is the cutaway actually cutting anything? Parked at the top of the world
    // it is inert, and saying so is what lets a cutting view behave EXACTLY as
    // an uncut one there — stamps included, which otherwise vanish, since a
    // stamp is drawn only where it reaches the plane and nothing reaches the
    // world's roof.
    public bool IsCutAway => CutawayY < _data.WorldMaxY;

    // The clip a view should draw at: its own plane if it cuts, else no clip.
    public int ClipFor(bool cuts) => cuts && IsCutAway ? CutawayY : int.MaxValue;
}
