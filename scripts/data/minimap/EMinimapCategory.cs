// What a voxel reads as on the in-game maps (HUD minimap + world map). The map
// shows CATEGORIES, not materials: a player reading it wants to know where the
// road, the water, the buildings and the things they cannot walk through are —
// not which of five soils they are standing on. Biome is carried by the region
// labels and by the world itself.
//
// Deliberately NOT BlockData.minimapColor, which stays as it is: that is the
// WORLD-MAP PAINTER's per-material authoring view (PaveTool / WaterTool option
// swatches, stamp plan colours), where telling marsh from desert is the whole
// point. Two consumers, two questions, two fields.
//
// Values are written into the surface texture's tile channel and index the
// category palette on Minimap, so they are a wire format of sorts — append
// rather than reorder.
public enum EMinimapCategory
{
    // Anything you walk on that is not one of the below: grass, soil, sand,
    // mud, marsh, snow. One colour, on purpose.
    Terrain = 0,
    // Rock, in the ground or built into a wall. Buildings read as stone because
    // they are made of stone tiles.
    Stone = 1,
    Water = 2,
    Road = 3,
    // Written by the prop pass rather than by a block — see Minimap's foliage
    // channel. Kept in the same enum so the palette is one table.
    Prop = 4,
}
