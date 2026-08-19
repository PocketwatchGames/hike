using Godot;

// Finds the short ledge in front of the player that an interact press should
// climb. Pure query over PlayerWalkField — it decides WHETHER and WHERE, never
// how; moving the player is the action's job.
//
// Anything the field reports as standable has already passed headroom and
// body-radius fit inside WalkabilityGrid.SampleColumn, so a candidate landing
// is a legitimate standing spot by construction and needs no separate clearance
// test here.
public static class MantleProbe
{
    public readonly struct Settings
    {
        // How far in front of the player to look for the ledge face, in metres.
        // Must clear the movement capsule's radius or the probe lands in the
        // column the player already occupies.
        public readonly float reach;
        // Rises at or below this are ordinary walking (step-up owns them) and
        // are not offered as a mantle. Zero when climbing out of water, where
        // there is no walking alternative — any reachable shore counts.
        public readonly float minRise;
        // Tallest ledge a mantle can take, up or down. Above this the player
        // needs a marked climbable surface, which is a separate affordance.
        public readonly float maxRise;
        // Whether a drop of the same size is also a candidate. Off when
        // swimming: climbing DOWN out of water is not a thing.
        public readonly bool allowDescend;
        // Which band to test first where a column offers both. The caller reads
        // it off what the player is FACING — rock at wall height means up, open
        // air means down — because the answer is otherwise arbitrary and the two
        // traversals go opposite ways.
        public readonly bool preferDescend;

        public Settings(float reach, float minRise, float maxRise, bool allowDescend,
            bool preferDescend = false)
        {
            this.reach = reach;
            this.minRise = minRise;
            this.maxRise = maxRise;
            this.allowDescend = allowDescend;
            this.preferDescend = preferDescend;
        }
    }

    public readonly struct Candidate
    {
        // Where the player ends up standing, centred in the landing cell.
        public readonly Vector3 landing;
        // Height change, in metres. Positive climbs, negative descends — drives
        // which mantle animation plays.
        public readonly float rise;

        public Candidate(Vector3 landing, float rise)
        {
            this.landing = landing;
            this.rise = rise;
        }
    }

    // facing is the horizontal direction the player is looking / moving; its Y
    // is ignored. Returns false when there is no ledge in the band ahead, which
    // is the common case and must stay cheap — it is one column sample.
    public static bool TryFind(PlayerWalkField field, Vector3 position, Vector3 facing,
        float refY, in Settings settings, out Candidate candidate)
    {
        candidate = default;

        Vector3 dir = new(facing.X, 0f, facing.Z);
        if (dir.LengthSquared() < 1e-6f)
        {
            return false;
        }
        dir = dir.Normalized();

        Vector3 ahead = position + dir * settings.reach;
        int wx = Mathf.FloorToInt(ahead.X);
        int wz = Mathf.FloorToInt(ahead.Z);

        // Exclusive at the near edge: a rise of exactly minRise is a step, not a
        // mantle, and offering both for the same ledge would make the prompt
        // flicker as the player walks up to it.
        const float BandEpsilon = 0.001f;

        // Up unless the caller says otherwise. A wall in front is the common
        // intent, and a column offering both an up and a down target (a ledge
        // with a terrace below) reads as "climb the wall" — but a player facing
        // out over open air means the opposite, which is what preferDescend
        // carries.
        bool descendFirst = settings.preferDescend && settings.allowDescend;

        if (!descendFirst && field.TryGetSurfaceInBand(wx, wz,
            refY + settings.minRise + BandEpsilon, refY + settings.maxRise, refY, out float upY))
        {
            candidate = new Candidate(new Vector3(wx + 0.5f, upY, wz + 0.5f), upY - refY);
            return true;
        }

        if (settings.allowDescend && field.TryGetSurfaceInBand(wx, wz,
            refY - settings.maxRise, refY - settings.minRise - BandEpsilon, refY, out float downY))
        {
            candidate = new Candidate(new Vector3(wx + 0.5f, downY, wz + 0.5f), downY - refY);
            return true;
        }

        // Only reachable when descent was preferred and found nothing — fall
        // back to the wall rather than refusing a traversal that exists.
        if (descendFirst && field.TryGetSurfaceInBand(wx, wz,
            refY + settings.minRise + BandEpsilon, refY + settings.maxRise, refY, out float upFallback))
        {
            candidate = new Candidate(new Vector3(wx + 0.5f, upFallback, wz + 0.5f), upFallback - refY);
            return true;
        }

        return false;
    }
}
