using Godot;

// What a ceiling-cutaway mask has to answer, whichever rule produced it.
//
// Two implementations at the moment: ClipColumnMask (the per-column band rule)
// and ClipCellMask (the cell-region rule). They disagree about almost everything
// upstream and about nothing downstream — the camera, the prop culling, the HUD
// gating and Roof all ask exactly these questions — so the mode switch lives in
// one place in GameClient and no consumer knows which rule is running.
public interface IClipMask
{
    // World Y that a column at zero height offset is cut above. Pushed to
    // camera_clip, so it also drives the iris animation and the cap plane.
    float ClipY { get; }
    // Whether anything overhead is actually being removed. False parks the whole
    // cutaway — and the indoor-mode signal the minimap reads off it — rather than
    // leaving it running inert.
    bool AnyClipped { get; }
    // False once the mask has smoothed shut, so the caller can drop the shader
    // term and its texture fetch instead of sampling an empty field.
    bool IsOpen { get; }
    // True on a tick where any column crossed the binary threshold or the window
    // scrolled. Prop culling skips static entities the rest of the time.
    bool MaskChanged { get; }

    // World XZ of the mask's (0,0) corner and its total world size.
    Vector2 OriginXz { get; }
    float Extent { get; }
    // Metres the mask's G channel spans above ClipY (clip_mask_height_span).
    // Zero when every participating column cuts at the same height.
    float HeightSpan { get; }
    Texture2D Texture { get; }

    // The clip height that applies at this point, given the caller's base — the
    // CPU-side twin of the shader's per-column height decode.
    float ClipHeightAt(Vector3 worldPosition, float baseClipY);
    // Does the cutaway remove geometry standing above the clip height here?
    bool IsClipped(Vector3 worldPosition);
    // How much of a footprint sits over the cut set, in [0,1]. Meshes that must
    // cut as ONE OBJECT (roofs, props) resolve participation through this rather
    // than sampling a point — no single point is right for a mesh spanning tens
    // of metres.
    float RegionCoverage(Vector2 minXz, Vector2 maxXz);
    // Can the mask have changed anything in this chunk's XZ footprint? Spans this
    // tick's window and last tick's, so a chunk the window just left is swept
    // once more and its props restored.
    bool WindowTouchesChunk(Vector3I chunkCoord);
    // One-line state dump for the debug cvars.
    string Describe(WorldState world, Vector3 playerPosition);
}
