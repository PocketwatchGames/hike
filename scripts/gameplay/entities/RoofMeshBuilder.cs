using Godot;
using System.Collections.Generic;

// The derived dimensions of a roof. Shared by the mesh builder and the editor's
// drag preview so the wireframe can't drift from the geometry it promises.
public readonly struct RoofDimensions
{
    // Unit vector along the ridge, and the one across it. `Across` is +Z for an
    // X seam but -X for a Z seam, deliberately: it keeps (edge direction × seam)
    // pointing the same way on both axes, so one winding rule serves both. Flip
    // it and every face on a Z-seam roof renders inside-out under cull_back.
    public readonly Vector3 Seam;
    public readonly Vector3 Across;
    // Where the vertical gable end face sits: flush with the footprint, i.e. on
    // the wall it rests on.
    public readonly float HalfSeamBody;
    // How far the sloped planes reach — they oversail the end face by the
    // style's rake overhang, so the roof visibly caps the wall rather than
    // stopping dead on it.
    public readonly float HalfSeam;
    public readonly float HalfAcross;
    public readonly float Rise;
    public readonly float Thickness;

    public RoofDimensions(RoofStyleData style, float sizeX, float sizeZ, ERoofSeamAxis seamAxis, float slopeDegrees)
    {
        // Never let a footprint collapse to nothing and fold the roof inside out.
        const float MIN_HALF_EXTENT = 0.25f;
        bool alongX = seamAxis == ERoofSeamAxis.AlongX;
        Seam = alongX ? Vector3.Right : Vector3.Back;
        Across = alongX ? Vector3.Back : Vector3.Left;
        HalfSeamBody = Mathf.Max((alongX ? sizeX : sizeZ) * 0.5f, MIN_HALF_EXTENT);
        HalfSeam = HalfSeamBody + style.rakeOverhang;
        HalfAcross = (alongX ? sizeZ : sizeX) * 0.5f + style.eaveOverhang;
        Rise = HalfAcross * Mathf.Tan(Mathf.DegToRad(slopeDegrees));
        Thickness = style.thickness;
    }
}

// Builds a roof's geometry from its footprint and pitch.
//
// The shape is a cross-section extruded along the seam axis. That structure is
// the point: a new roof FORM (shed, hip, mansard) is a new cross-section and
// nothing else — winding, normals, UVs, caps and collision are form-agnostic.
//
// It extrudes in two parts, which is what produces the rake overhang:
//   * BODY, out to HalfSeamBody — the full cross-section, filled down to a flat
//     soffit. Ends in the vertical gable face, flush with the wall.
//   * RAKE, from there out to HalfSeam — only the sloped slab continues, so the
//     roof oversails the gable end and you read its underside from below.
//
// Two things about the vertical layout are load-bearing:
//   * Y = 0 is the eave's UNDERSIDE, so the roof sits ON the surface it was
//     dragged over instead of sinking its own thickness into it.
//   * The body's underside is FLAT rather than parallel to the slopes. That is
//     what lets the roof clip at its base: GameCamera derives the cutaway from
//     where its upward ray hits, so a flat soffit reports the same elevation
//     from anywhere inside. Follow the slopes instead and standing beneath the
//     ridge yields a clip well above the eave, leaving the roof drawn over the
//     player it should have revealed.
//
// UVs are generated in world meters (× the style's tiling rate) because
// model_lit samples raw UV with no scale uniform: tiling has to be baked in
// here or a roof stretches one texture across its whole span.
public static class RoofMeshBuilder
{
    // Edges shorter than this are skipped — a zero-length edge has no direction
    // to derive an outward normal from.
    private const float EDGE_EPSILON = 1e-4f;

    // Lifts the soffit clear of whatever the roof was dragged onto. A roof eave
    // lands exactly on the top of the wall voxels it covers, and two coplanar
    // surfaces z-fight. Not an [Export]: it's a depth bias, not a design knob,
    // and it sits below the 0.001 spinbox step where a value would snap.
    private const float SOFFIT_LIFT = 0.02f;

    // Floor on the authored lip run. The lip has to lean at least slightly or
    // its end-cap span is zero-width, and the degenerate sliver leaves
    // neighbouring spans meeting the sides at a T-junction free to crack open.
    private const float MIN_LIP_RUN_FRACTION = 0.02f;

    public static ArrayMesh Build(RoofStyleData style, float sizeX, float sizeZ, ERoofSeamAxis seamAxis, float slopeDegrees)
    {
        var size = new RoofDimensions(style, sizeX, sizeZ, seamAxis, slopeDegrees);
        float tiles = style.textureTilesPerMeter;

        // Chamfered up front, because the profile feeds the sides AND both sets
        // of caps — bevelling any one of them alone would tear them apart.
        Vector2[] top = Chamfer(BuildTopProfile(size, style), style.edgeBevel, size.Thickness,
            Mathf.Cos(Mathf.DegToRad(style.bevelMinTurnDegrees)));
        // The slab's underside, and the body's flat soffit. Clamped rather than
        // simply offset: near the eaves the chamfer takes the profile low enough
        // that a raw offset would dive under the soffit and invert the slab.
        var under = new Vector2[top.Length];
        var soffit = new Vector2[top.Length];
        for (int i = 0; i < top.Length; i++)
        {
            float underY = Mathf.Max(top[i].Y - size.Thickness, SOFFIT_LIFT);
            // SNAP when the slab bottoms out on the soffit. Where the eave
            // corner escapes the chamfer (a steep pitch), top.Y - thickness
            // lands a few ULPs ABOVE the constant rather than below it, so Max
            // returns the computed value and the two chains hold points that are
            // equal to the eye but not to ==. The caps then emit a vertex that
            // misses the sides' by nanometres, leaving a hairline seam right
            // along the eave. Collapsing them to the identical value is what
            // keeps the two surfaces sharing real vertices.
            if (underY - SOFFIT_LIFT < EDGE_EPSILON)
            {
                underY = SOFFIT_LIFT;
            }
            under[i] = new Vector2(top[i].X, underY);
            soffit[i] = new Vector2(top[i].X, SOFFIT_LIFT);
        }

        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        var context = new Context(surface, size, style, tiles);

        // Sides. The body carries the full cross-section; the two rake segments
        // carry only the slab, so the soffit stops at the wall.
        context.ExtrudeOutline(Close(top, soffit), -size.HalfSeamBody, size.HalfSeamBody);
        context.ExtrudeOutline(Close(top, under), size.HalfSeamBody, size.HalfSeam);
        context.ExtrudeOutline(Close(top, under), -size.HalfSeam, -size.HalfSeamBody);

        // Caps. The slab's own ends close the oversail; the region between the
        // slab underside and the soffit is the vertical gable face at the wall,
        // which tapers to nothing at the eaves where the two chains meet.
        context.AddChainCap(top, under, size.HalfSeam, facingPositive: true);
        context.AddChainCap(top, under, -size.HalfSeam, facingPositive: false);
        context.AddChainCap(under, soffit, size.HalfSeamBody, facingPositive: true);
        context.AddChainCap(under, soffit, -size.HalfSeamBody, facingPositive: false);

        // No GenerateNormals: the faces are flat and authored, and averaging
        // them would round off the ridge and the eave corners. Tangents ARE
        // generated — roof_lit reads a tangent-space normal map, and without
        // them the frame is undefined and the relief goes to noise.
        surface.GenerateTangents();
        return surface.Commit();
    }

    // Closes an upper and a lower chain (shared `across` values) into one
    // outline, wound CLOCKWISE in (across, up): left-to-right along the top,
    // right-to-left along the bottom. The two end segments become the eave
    // fascias. The normal rule in ExtrudeOutline assumes that winding.
    private static Vector2[] Close(Vector2[] upper, Vector2[] lower)
    {
        var outline = new Vector2[upper.Length * 2];
        for (int i = 0; i < upper.Length; i++)
        {
            outline[i] = upper[i];
            outline[upper.Length + i] = lower[lower.Length - 1 - i];
        }
        return outline;
    }

    // Everything that needs the roof's basis and tuning to emit a face. A struct
    // purely so the emit helpers don't take eight arguments each.
    private readonly struct Context
    {
        private readonly SurfaceTool _surface;
        private readonly RoofDimensions _size;
        private readonly float _tiles;
        private readonly float _eaveShade;
        private readonly float _ridgeY;

        public Context(SurfaceTool surface, RoofDimensions size, RoofStyleData style, float tiles)
        {
            _surface = surface;
            _size = size;
            _tiles = tiles;
            _eaveShade = style.eaveShade;
            _ridgeY = SOFFIT_LIFT + style.thickness + size.Rise;
        }

        private Vector3 Point(Vector2 section, float alongSeam)
        {
            return _size.Across * section.X + Vector3.Up * section.Y + _size.Seam * alongSeam;
        }

        // Baked tone: full brightness at the ridge falling to eaveShade at the
        // edges, so a big roof isn't uniformly flat. model_lit reads COLOR.r as
        // occlusion (1 = open), gated by the material's vertex_ao_strength.
        private float Shade(Vector2 section)
        {
            float span = _ridgeY - SOFFIT_LIFT;
            float t = span > EDGE_EPSILON ? Mathf.Clamp((section.Y - SOFFIT_LIFT) / span, 0f, 1f) : 1f;
            return Mathf.Lerp(_eaveShade, 1f, t);
        }

        // One extruded quad per outline edge, between two offsets along the seam.
        // `arc` runs the texture continuously up one slope and over the ridge
        // rather than restarting at each face, so courses line up across the seam.
        public void ExtrudeOutline(Vector2[] outline, float seamFrom, float seamTo)
        {
            float arc = 0f;
            for (int i = 0; i < outline.Length; i++)
            {
                Vector2 a = outline[i];
                Vector2 b = outline[(i + 1) % outline.Length];
                Vector2 edge = b - a;
                float length = edge.Length();
                if (length <= EDGE_EPSILON)
                {
                    continue;
                }
                // Outward normal of a clockwise outline: the edge direction
                // turned a quarter turn counter-clockwise.
                Vector2 normal2D = new Vector2(-edge.Y, edge.X) / length;
                Vector3 normal = _size.Across * normal2D.X + Vector3.Up * normal2D.Y;
                AddQuad(normal, Shade(a), Shade(b), Shade(b), Shade(a),
                    Point(a, seamFrom), Point(b, seamFrom), Point(b, seamTo), Point(a, seamTo),
                    new Vector2(arc, seamFrom) * _tiles, new Vector2(arc + length, seamFrom) * _tiles,
                    new Vector2(arc + length, seamTo) * _tiles, new Vector2(arc, seamTo) * _tiles);
                arc += length;
            }
        }

        // Fills the region between two chains sharing the same `across` values,
        // as one span per segment. NOT a fan: once courses step the top edge the
        // cross-section stops being star-shaped from any corner, and triangles
        // spanning a riser come out inverted and invisible. Spans only need the
        // chains to advance monotonically in `across`.
        //
        // Either chain may touch the other — the gable face pinches to nothing
        // at the eaves — so a collapsed end degrades to a triangle rather than a
        // zero-area sliver, which would leave the cap unsealed against the sides.
        public void AddChainCap(Vector2[] upper, Vector2[] lower, float alongSeam, bool facingPositive)
        {
            Vector3 normal = facingPositive ? _size.Seam : -_size.Seam;
            for (int i = 0; i < upper.Length - 1; i++)
            {
                Vector2 upperA = upper[i];
                Vector2 upperB = upper[i + 1];
                Vector2 lowerA = lower[i];
                Vector2 lowerB = lower[i + 1];
                bool collapsedA = upperA.DistanceSquaredTo(lowerA) <= EDGE_EPSILON * EDGE_EPSILON;
                bool collapsedB = upperB.DistanceSquaredTo(lowerB) <= EDGE_EPSILON * EDGE_EPSILON;
                if (collapsedA && collapsedB)
                {
                    continue;
                }
                if (collapsedA)
                {
                    AddCapTriangle(normal, facingPositive, alongSeam, upperA, upperB, lowerB);
                }
                else if (collapsedB)
                {
                    AddCapTriangle(normal, facingPositive, alongSeam, upperA, upperB, lowerA);
                }
                else
                {
                    // Corner order that faces +seam; reversed for the far end.
                    if (facingPositive)
                    {
                        AddQuad(normal, Shade(upperA), Shade(upperB), Shade(lowerB), Shade(lowerA),
                            Point(upperA, alongSeam), Point(upperB, alongSeam),
                            Point(lowerB, alongSeam), Point(lowerA, alongSeam),
                            upperA * _tiles, upperB * _tiles, lowerB * _tiles, lowerA * _tiles);
                    }
                    else
                    {
                        AddQuad(normal, Shade(lowerA), Shade(lowerB), Shade(upperB), Shade(upperA),
                            Point(lowerA, alongSeam), Point(lowerB, alongSeam),
                            Point(upperB, alongSeam), Point(upperA, alongSeam),
                            lowerA * _tiles, lowerB * _tiles, upperB * _tiles, upperA * _tiles);
                    }
                }
            }
        }

        private void AddCapTriangle(Vector3 normal, bool facingPositive, float alongSeam, Vector2 a, Vector2 b, Vector2 c)
        {
            if (facingPositive)
            {
                AddTriangle(normal, Shade(a), Shade(b), Shade(c),
                    Point(a, alongSeam), Point(b, alongSeam), Point(c, alongSeam),
                    a * _tiles, b * _tiles, c * _tiles);
            }
            else
            {
                AddTriangle(normal, Shade(c), Shade(b), Shade(a),
                    Point(c, alongSeam), Point(b, alongSeam), Point(a, alongSeam),
                    c * _tiles, b * _tiles, a * _tiles);
            }
        }

        private void AddQuad(Vector3 normal, float shadeA, float shadeB, float shadeC, float shadeD,
            Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD)
        {
            AddTriangle(normal, shadeA, shadeB, shadeC, a, b, c, uvA, uvB, uvC);
            AddTriangle(normal, shadeA, shadeC, shadeD, a, c, d, uvA, uvC, uvD);
        }

        // Callers pass corners counter-clockwise as seen from outside (the order
        // the outward normal falls out of), and this reverses them on the way in.
        //
        // Godot front-faces are CLOCKWISE — the opposite of the OpenGL default —
        // so for a correctly wound triangle (v1-v0)x(v2-v0) points INWARD,
        // against the surface normal. Measured, not assumed: BoxMesh and
        // SphereMesh disagree with their own normals on 100% of faces. Emit the
        // intuitive order and every face is back-facing, so the mesh culls to
        // nothing while its normals still look correct in any self-check.
        private void AddTriangle(Vector3 normal, float shadeA, float shadeB, float shadeC,
            Vector3 a, Vector3 b, Vector3 c, Vector2 uvA, Vector2 uvB, Vector2 uvC)
        {
            AddVertex(normal, shadeA, a, uvA);
            AddVertex(normal, shadeC, c, uvC);
            AddVertex(normal, shadeB, b, uvB);
        }

        private void AddVertex(Vector3 normal, float shade, Vector3 position, Vector2 uv)
        {
            // Attributes before the vertex — SurfaceTool latches whatever was
            // last set when AddVertex runs.
            _surface.SetNormal(normal);
            _surface.SetUV(uv);
            _surface.SetColor(new Color(shade, shade, shade, 1f));
            _surface.AddVertex(position);
        }
    }

    // Top surface, eave → ridge → eave. Each course climbs most of its rise
    // along the slope and then steps up by a lip, so the roof reads as banded
    // rows of shingles instead of one flat plane. The mean pitch is unchanged —
    // the lip comes out of the course's own rise, not on top of it.
    private static Vector2[] BuildTopProfile(in RoofDimensions size, RoofStyleData style)
    {
        float baseY = SOFFIT_LIFT + size.Thickness;
        int courses = style.coursesPerMeter > 0f
            ? Mathf.Max(1, Mathf.CeilToInt(size.HalfAcross * style.coursesPerMeter))
            : 1;
        float run = size.HalfAcross / courses;
        float climb = size.Rise / courses;
        // A lip at or above the course's own rise would turn the slope into a
        // staircase (or invert it), so it can never take the whole climb.
        float lip = Mathf.Min(style.courseLipHeight, climb * 0.5f);

        var half = new List<Vector2>(courses * 2 + 1) { new Vector2(-size.HalfAcross, baseY) };
        for (int i = 1; i <= courses; i++)
        {
            float u = -size.HalfAcross + run * i;
            float topY = baseY + climb * i;
            // The topmost course ends AT the ridge with no lip. A lip there gets
            // mirrored onto the far slope as a second riser at the same `across`,
            // folding the profile back over itself into a zero-area crease that
            // tears the caps and inverts the faces around the peak.
            if (i == courses)
            {
                half.Add(new Vector2(u, topY));
                break;
            }
            float lipRun = Mathf.Max(style.courseLipRun, MIN_LIP_RUN_FRACTION);
            half.Add(new Vector2(u - run * lipRun, topY - lip));
            half.Add(new Vector2(u, topY));
        }

        // Mirror for the far slope so both sides step identically and the ridge
        // stays a single shared point.
        var profile = new List<Vector2>(half.Count * 2 - 1);
        profile.AddRange(half);
        for (int i = half.Count - 2; i >= 0; i--)
        {
            profile.Add(new Vector2(-half[i].X, half[i].Y));
        }
        return profile.ToArray();
    }

    // Cuts the profile's sharp corners back into short chamfer segments: the
    // ridge, and the two eave corners where the top surface turns down onto the
    // fascia. Course lips are deliberately left sharp — their corners are
    // shallow, and a bevel wide enough to read at the ridge would erase them.
    private static Vector2[] Chamfer(Vector2[] profile, float bevel, float fasciaHeight, float sharpTurnDot)
    {
        if (bevel <= EDGE_EPSILON || profile.Length < 2)
        {
            return profile;
        }
        var result = new List<Vector2>(profile.Length + 4);
        int last = profile.Length - 1;
        // Stand-ins for the fascia each end turns down onto, so the eave corners
        // chamfer through the same path as every interior corner.
        Vector2 beforeFirst = profile[0] - new Vector2(0f, fasciaHeight);
        Vector2 afterLast = profile[last] - new Vector2(0f, fasciaHeight);
        for (int i = 0; i <= last; i++)
        {
            Vector2 prev = i == 0 ? beforeFirst : profile[i - 1];
            Vector2 next = i == last ? afterLast : profile[i + 1];
            AddChamferedCorner(result, prev, profile[i], next, bevel, sharpTurnDot);
        }
        return result.ToArray();
    }

    private static void AddChamferedCorner(List<Vector2> into, Vector2 prev, Vector2 corner, Vector2 next, float bevel, float sharpTurnDot)
    {
        Vector2 dirIn = (corner - prev).Normalized();
        Vector2 dirOut = (next - corner).Normalized();
        // A turn shallower than the style's threshold isn't an edge worth cutting.
        if (dirIn.Dot(dirOut) > sharpTurnDot)
        {
            into.Add(corner);
            return;
        }
        // Never eat more than half of either neighbouring edge, or a fine course
        // spacing would let adjacent chamfers cross over each other.
        float back = Mathf.Min(bevel, Mathf.Min(prev.DistanceTo(corner), corner.DistanceTo(next)) * 0.45f);
        into.Add(corner - dirIn * back);
        into.Add(corner + dirOut * back);
    }
}
