using Godot;

// Stage 2 visualisation for the iris cutaway's probe ring. Touches no clip path.
//
// It exists to answer three questions by eye, because all three produce the same
// symptom once geometry starts disappearing and are hard to tell apart from a
// number:
//
//   Does the ring REACH? A doorway has to register before the player is on top of
//   it, and the radii are the only thing that decides that. Blocked samples are
//   drawn precisely so a ring being eaten by a wall is visible.
//   Is the base height STABLE? The stem drawn at each sample plus the plane
//   crossing them shows the spread the quantile is picking out of; a base that
//   twitches while standing still is a spread problem, not a tuning one.
//   Does OCCLUSION mean what it should? Standing behind a tree must not light the
//   ring up, and standing behind a building must.
public static class ClipIrisDebug
{
    public enum ELevel
    {
        Off = 0,
        // The ring: one marker per sample, a stem to its ceiling, occlusion state.
        Probes = 1,
        // Plus the resolved base plane and the disc seed the ring picked.
        Resolved = 2,
    }

    // Air at the player's level with a real ceiling — the ordinary case.
    private static readonly Color OpenColor = new(0.35f, 0.85f, 1f);
    // Solid at the player's level. Grey because it is not a finding, it is the
    // ring being unable to answer here.
    private static readonly Color BlockedColor = new(0.4f, 0.4f, 0.45f);
    private static readonly Color SkyColor = new(0.25f, 0.4f, 0.6f);
    // Hidden from the camera. Warm against the cool ring so a lit-up ring reads at
    // a glance without having to count markers.
    private static readonly Color OccludedColor = new(1f, 0.55f, 0.15f);
    private static readonly Color BaseColor = new(0.6f, 1f, 0.5f);
    // The disc's seed — the one sample the reveal would grow from.
    private static readonly Color SeedColor = new(1f, 0.25f, 0.55f);
    // The disk, and the same disk while the base elevation is latched down.
    private static readonly Color DiscColor = new(1f, 0.9f, 0.3f);
    private static readonly Color LatchedColor = new(1f, 0.35f, 0.2f);

    private const float MARKER_SIZE = 0.35f;
    private const float SEED_SIZE = 0.8f;
    private const float OCCLUDED_TICK = 0.6f;
    // Radius of the ring drawn at the resolved base height.
    private const float BASE_RING_RADIUS = 6f;
    private const int BASE_RING_SEGMENTS = 32;

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
                ClipIris.EProbeSpace.Sky => SkyColor,
                _ => OpenColor,
            };
            DebugDraw.Cross(probe.Point, MARKER_SIZE, color);

            // A stem from the sample up to what it found. The ceiling is the
            // whole measurement, so drawing it as a height rather than a colour
            // is what makes a stray low sample obvious among its neighbours.
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
            }
        }

        if (level < ELevel.Resolved)
        {
            return;
        }

        // The base plane the quantile settled on, drawn where the samples that
        // voted for it are, so a plane sitting above the ceiling you are standing
        // under is visible as a plane and not inferred from a number.
        if (!float.IsPositiveInfinity(iris.BaseCeilingY))
        {
            DrawRing(new Vector3(playerPosition.X, iris.BaseCeilingY, playerPosition.Z),
                BASE_RING_RADIUS, BaseColor);
        }

        int seed = iris.NearestLowProbe;
        if (seed >= 0 && seed < probes.Length)
        {
            ClipIris.Probe probe = probes[seed];
            DebugDraw.Cross(probe.Point, SEED_SIZE, SeedColor);
            DebugDraw.Line(playerPosition, probe.Point, SeedColor);
        }

        // The disk itself. Drawn in the CAMERA PLANE, not flat on the ground —
        // that is literally what it is, so a flat ring would be a picture of a
        // different shape than the one being cut.
        if (iris.DiscActive)
        {
            Color color = iris.BaseLatched ? LatchedColor : DiscColor;
            DrawScreenRing(iris.DiscCenter, iris.ScreenRight, iris.ScreenUp, iris.DiscRadius, color);
        }
    }

    // Flat ring on the ground plane, for the base height.
    private static void DrawRing(Vector3 center, float radius, Color color)
    {
        float step = Mathf.Tau / BASE_RING_SEGMENTS;
        for (int i = 0; i < BASE_RING_SEGMENTS; i++)
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
        float step = Mathf.Tau / BASE_RING_SEGMENTS;
        for (int i = 0; i < BASE_RING_SEGMENTS; i++)
        {
            Vector3 a = center + (right * Mathf.Cos(i * step) + up * Mathf.Sin(i * step)) * radius;
            Vector3 b = center + (right * Mathf.Cos((i + 1) * step) + up * Mathf.Sin((i + 1) * step)) * radius;
            DebugDraw.Line(a, b, color);
        }
    }
}
