using Godot;

// Visualisation for the cutaway's probe ring. Touches no clip path.
//
// It exists to answer three things by eye, because all three produce the same
// symptom once geometry starts disappearing and are hard to tell apart from a
// number:
//
//   Which samples are VOTING? Only plain-air columns do. Doorways and windows are
//   drawn in their own colour precisely so a plane that dips near a door can be
//   traced to the columns that caused it — or ruled out at a glance.
//   Does the ring REACH? Its radius breathes with whether the player is hidden,
//   so seeing where the samples actually land is the only way to judge the range.
//   Is OCCLUSION right? Standing behind a tree must not light the ring up;
//   standing behind a building must, and the disk must grow to cover it.
public static class ClipIrisDebug
{
    public enum ELevel
    {
        Off = 0,
        // The ring: one marker per sample, a stem to its ceiling, occlusion state.
        Probes = 1,
        // Plus the two resolved planes and the disk.
        Resolved = 2,
    }

    // Plain air with a ceiling — the only samples that vote on height.
    private static readonly Color OpenColor = new(0.35f, 0.85f, 1f);
    private static readonly Color SkyColor = new(0.25f, 0.4f, 0.6f);
    // A doorway or window. Its own colour because it is the one exclusion that
    // has to be verifiable by eye.
    private static readonly Color OpeningColor = new(0.9f, 0.45f, 1f);
    // Under a roof's overhang only — cover, but neither a room nor open sky.
    private static readonly Color OversailColor = new(0.95f, 0.75f, 0.4f);
    // Solid at the player's level. Grey because it is not a finding, it is the
    // ring being unable to answer here.
    private static readonly Color BlockedColor = new(0.4f, 0.4f, 0.45f);
    // Out of the player's own sight — behind a wall, inside the building next door.
    // The sample exists but describes space the player has no business revealing, so
    // it votes on nothing.
    private static readonly Color HiddenColor = new(0.22f, 0.18f, 0.28f);
    // Hidden from the camera. Warm against the cool ring so a lit-up ring reads at
    // a glance without having to count markers.
    private static readonly Color OccludedColor = new(1f, 0.55f, 0.15f);
    // Where the camera ray actually stopped, and the ray to it. Red so it reads
    // against both the cool ring and the warm occlusion ticks.
    private static readonly Color BlockerColor = new(1f, 0.25f, 0.2f);
    // The player's own camera march when it finds nothing. Cool and dim so a clear
    // ray recedes and a blocked one reads immediately.
    private static readonly Color ClearRayColor = new(0.3f, 0.55f, 0.7f);
    private static readonly Color BaseColor = new(0.6f, 1f, 0.5f);
    private static readonly Color IrisColor = new(1f, 0.9f, 0.3f);

    private const float MARKER_SIZE = 0.35f;
    private const float OCCLUDED_TICK = 0.6f;
    private const float BASE_RING_RADIUS = 6f;
    private const int RING_SEGMENTS = 32;

    public static void Draw(ClipIris iris, Vector3 playerPosition, ELevel level)
    {
        if (iris == null || level == ELevel.Off)
        {
            return;
        }

        System.ReadOnlySpan<ClipIris.Probe> probes = iris.Probes;
        for (int i = 0; i < probes.Length; i++)
        {
            ClipIris.Probe probe = probes[i];
            Color color = probe.Space switch
            {
                ClipIris.EProbeSpace.Blocked => BlockedColor,
                ClipIris.EProbeSpace.Opening => OpeningColor,
                ClipIris.EProbeSpace.Oversail => OversailColor,
                ClipIris.EProbeSpace.Sky => SkyColor,
                _ => OpenColor,
            };
            // Blocked first, and at full size: it is culled before the visibility
            // march ever runs, so its Visible flag means "not asked" rather than
            // "no". Drawn grey as always — the ring being unable to answer here is a
            // different fact from the ring reaching somewhere it shouldn't, and the
            // two want to stay tellable apart.
            if (probe.Space == ClipIris.EProbeSpace.Blocked)
            {
                DebugDraw.Cross(probe.Point, MARKER_SIZE, BlockedColor);
                continue;
            }
            // Samples the player cannot see are drawn small and dark, with no stem.
            // Reading a ring of bright stems standing INSIDE a building is how the
            // through-the-wall sampling was spotted, so the fix has to be just as
            // visible: those samples must now recede instead of reporting.
            if (!probe.Visible)
            {
                DebugDraw.Cross(probe.Point, MARKER_SIZE * 0.4f, HiddenColor);
                continue;
            }
            DebugDraw.Cross(probe.Point, MARKER_SIZE, color);

            // A stem up to what it found. The ceiling is the whole measurement, so
            // drawing it as a height is what makes a stray low sample obvious
            // among its neighbours.
            if (probe.Space == ClipIris.EProbeSpace.Open)
            {
                var top = new Vector3(probe.Point.X, probe.CeilingY, probe.Point.Z);
                DebugDraw.Line(probe.Point, top, color);
                DebugDraw.Cross(top, MARKER_SIZE * 0.7f, color);
            }

            // Occlusion is drawn as a mark ABOVE the sample rather than by
            // recolouring it, so the two queries stay independently readable —
            // they disagree often, and that disagreement is the interesting part.
            if (probe.Occluded)
            {
                Vector3 tick = probe.Point + Vector3.Up * (OCCLUDED_TICK * 0.5f);
                DebugDraw.Line(probe.Point, tick, OccludedColor);
                DebugDraw.Cross(tick, MARKER_SIZE * 0.5f, OccludedColor);
                // The ray itself, and what stopped it. WHICH samples are occluded
                // was never the hard question — WHAT is occluding them is, and
                // without drawing it the answer is guesswork off a screenshot. The
                // origin leg is drawn too because the ray starts RAISED off the body,
                // which is the part nobody remembers.
                DebugDraw.Line(probe.Point, probe.OcclusionFrom, BlockerColor);
                DebugDraw.Line(probe.OcclusionFrom, probe.OcclusionHit, BlockerColor);
                DebugDraw.Cross(probe.OcclusionHit, MARKER_SIZE, BlockerColor);
            }
        }

        // The player-hidden LADDER — one ray per rung, eye upward, unraised. Between
        // them they decide how far the reach eases from small to large, so seeing
        // which rungs clear is seeing the reason for the size. Drawn separately from
        // the ring because the two answer different questions: the ring's samples are
        // raised so a terrace cannot latch them, these are not, because "am I behind
        // something" has no such exemption. Reading the ring's raised rays as though
        // they were these is exactly how the large latch got misdiagnosed.
        System.ReadOnlySpan<ClipIris.HiddenRay> rungs = iris.PlayerHiddenRays;
        for (int i = 0; i < rungs.Length; i++)
        {
            ClipIris.HiddenRay rung = rungs[i];
            DebugDraw.Line(rung.From, rung.Hit, rung.Blocked ? BlockerColor : ClearRayColor);
            if (rung.Blocked)
            {
                DebugDraw.Cross(rung.Hit, MARKER_SIZE, BlockerColor);
            }
        }

        if (level < ELevel.Resolved)
        {
            return;
        }

        // The voted ceiling, drawn flat where the samples that chose it are, so a
        // plane sitting above the ceiling you are standing under reads as a plane
        // rather than having to be inferred from a number.
        if (!float.IsPositiveInfinity(iris.BaseClipY))
        {
            DrawFlatRing(new Vector3(playerPosition.X, iris.BaseClipY, playerPosition.Z),
                BASE_RING_RADIUS, BaseColor);
        }

        if (!iris.IrisActive)
        {
            return;
        }
        // The disk, in the CAMERA PLANE — that is literally what it is, so a flat
        // ring would be a picture of a different shape than the one being cut.
        DrawScreenRing(iris.IrisCenter, iris.ScreenRight, iris.ScreenUp, iris.IrisRadius, IrisColor);
        // The plane, so the two elevations can be compared directly.
        DrawFlatRing(new Vector3(playerPosition.X, iris.IrisClipY, playerPosition.Z),
            MARKER_SIZE * 6f, IrisColor);
    }

    private static void DrawFlatRing(Vector3 center, float radius, Color color)
    {
        float step = Mathf.Tau / RING_SEGMENTS;
        for (int i = 0; i < RING_SEGMENTS; i++)
        {
            var a = center + new Vector3(Mathf.Cos(i * step), 0f, Mathf.Sin(i * step)) * radius;
            var b = center + new Vector3(Mathf.Cos((i + 1) * step), 0f, Mathf.Sin((i + 1) * step)) * radius;
            DebugDraw.Line(a, b, color);
        }
    }

    // Ring in the camera's own plane — the disk as the shaders measure it, so it
    // reads as a true circle on screen whatever the camera yaw.
    private static void DrawScreenRing(Vector3 center, Vector3 right, Vector3 up, float radius, Color color)
    {
        float step = Mathf.Tau / RING_SEGMENTS;
        for (int i = 0; i < RING_SEGMENTS; i++)
        {
            Vector3 a = center + (right * Mathf.Cos(i * step) + up * Mathf.Sin(i * step)) * radius;
            Vector3 b = center + (right * Mathf.Cos((i + 1) * step) + up * Mathf.Sin((i + 1) * step)) * radius;
            DebugDraw.Line(a, b, color);
        }
    }
}
