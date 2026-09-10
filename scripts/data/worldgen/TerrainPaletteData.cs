using Godot;

// This world's terrain palette: slot -> TerrainData.
//
// **THIS IS A WIRE FORMAT.** `ChunkState.TerrainId` is one byte per voxel
// holding an INDEX into this array, and every `.hike` ever baked stores those
// bytes. So the array has rules an ordinary authored list does not:
//
//   - **APPEND ONLY.** Inserting, removing or reordering an entry silently
//     re-textures every world already baked against it. No version fingerprint
//     catches that on its own — the stored bytes are still perfectly valid, they
//     just mean a different terrain now. (`WorldFile` records the slot names and
//     refuses a world whose palette moved, which is what turns that silent
//     re-texture into an error you can read.)
//   - **A terrain appears once.** Two slots naming the same terrain waste one and make
//     "which slot is this terrain" arbitrary.
//   - **256 slots**, because the channel is a byte. `TerrainPalette.Build` warns.
//
// It is AUTHORED rather than derived, and that is the point of the resource.
// The palette used to be built by walking `WorldGenData.zones` and collecting
// each zone's four terrain slots in declaration order, which made the wire format a
// side effect of where zones happen to be placed: adding or reordering a zone
// re-textured every baked world, and a terrain that no zone referenced had no slot
// at all — so anything naming it fell back to slot 0 and came out as some other
// material. The world-map painter hit that second failure with three swamp
// ground sets, which is what made it worth fixing rather than documenting.
[GlobalClass]
public partial class TerrainPaletteData : Resource
{
    [Export] public TerrainData[] terrains = System.Array.Empty<TerrainData>();
}
