using System.Collections.Generic;
using Godot;

// In-memory container for an authored voxel subscene — a small voxel
// region (e.g. a cottage, a dungeon room) that can be stamped into a
// WorldState during worldgen. Distinct from ChunkState because:
//   - Size is arbitrary (not 16³).
//   - Coordinates are subscene-local; an Anchor offset locates the
//     placement reference inside the bbox so a stamp position can mean
//     "the door's floor center" rather than "the bbox min corner."
//   - A presence mask flags which cells of the bbox actually belong to
//     the subscene; cells outside the mask don't overwrite destination
//     voxels on stamp (lets you author interior air without nuking
//     surrounding terrain at the bbox boundary).
//   - Optional environmental channels (Wind, EnvTag) are stored at
//     subscene-cell resolution and only override the destination where
//     authored — so a dungeon can force its own space class without
//     bringing wind data along, etc.
//
// Channels backed by always-on byte arrays (Voxels, Shape, TerrainId,
// OverlayId, DetailGroup, DetailStrength) and an always-on PresenceMask.
// Optional channels (Interiorness, EnvTag) are null unless authored.
public class SubsceneState
{
    public readonly Vector3I Size;

    // Subscene-local position used as the placement reference. A stamp
    // position of (wx, wy, wz) places the subscene so that local cell
    // floor(Anchor) lands at (wx, wy, wz). Stored as Vector3 (not Vector3I)
    // so the anchor can sit between voxels (e.g. on a doorway center).
    //
    // SubsceneBuilder puts Y on the authoring world's y=0 plane, so the anchor
    // is generally INSIDE the bbox rather than at its floor — content authored
    // below y=0 has a positive Anchor.Y and stamps below the destination ground.
    // Anything deriving the bbox corner from a stamp position must subtract the
    // whole anchor (SubsceneStamper.ComputeWorldOrigin), never assume Y is 0.
    public Vector3 Anchor;

    // Voxel channels — sized [Size.X, Size.Y, Size.Z], row-major X,Y,Z.
    public readonly byte[,,] Voxels;
    public readonly byte[,,] Shape;
    public readonly byte[,,] TerrainId;
    public readonly byte[,,] OverlayId;
    public readonly byte[,,] DetailGroup;
    public readonly byte[,,] DetailStrength;

    // Which faces each voxel's OverlayId dresses (EVoxelFace bits; 0 = all).
    // Lazy for the same reason as ChunkState.OverlayFaces — most scenes carry
    // none. Rotating a scene must PERMUTE these bits; see SubsceneRotator.
    public byte[,,] OverlayFaces;

    // Per-voxel "this cell belongs to the subscene." Cells with mask=false
    // are skipped on stamp — the destination voxel is left untouched.
    public readonly bool[,,] PresenceMask;

    // Optional env subgrids. Sized [envSize.X, envSize.Y, envSize.Z]
    // where envSize = ceil(Size / ENV_VOXELS_PER_CELL). Each cell covers
    // a 4³ subscene-voxel cube. Null when the subscene didn't author
    // overrides — the stamper leaves the destination subgrid alone.
    public byte[,,] Interiorness;
    public byte[,,] EnvTag;

    public List<EntitySimState> Entities = new();

    public SubsceneState(Vector3I size)
    {
        Size = size;
        Voxels = new byte[size.X, size.Y, size.Z];
        Shape = new byte[size.X, size.Y, size.Z];
        TerrainId = new byte[size.X, size.Y, size.Z];
        OverlayId = new byte[size.X, size.Y, size.Z];
        DetailGroup = new byte[size.X, size.Y, size.Z];
        DetailStrength = new byte[size.X, size.Y, size.Z];
        PresenceMask = new bool[size.X, size.Y, size.Z];
    }

    public Vector3I EnvSize
    {
        get
        {
            int s = ChunkState.ENV_VOXELS_PER_CELL;
            return new Vector3I(
                (Size.X + s - 1) / s,
                (Size.Y + s - 1) / s,
                (Size.Z + s - 1) / s);
        }
    }

    public void EnsureInteriorness()
    {
        if (Interiorness == null)
        {
            Vector3I es = EnvSize;
            Interiorness = new byte[es.X, es.Y, es.Z];
        }
    }

    public void EnsureEnvTag()
    {
        if (EnvTag == null)
        {
            Vector3I es = EnvSize;
            EnvTag = new byte[es.X, es.Y, es.Z];
        }
    }

    public void EnsureOverlayFaces()
    {
        if (OverlayFaces == null)
        {
            OverlayFaces = new byte[Size.X, Size.Y, Size.Z];
        }
    }
}
