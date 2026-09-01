using Godot;

// The path a body takes while something other than steering owns its position
// for a ledge traversal.
//
// The two axes are deliberately out of phase: going UP the rise leads and the
// forward translation trails, so the body clears the lip instead of cutting
// through its corner; coming DOWN they swap, so it steps out over the edge
// before it drops. Lerping both together is what makes a mantle look like the
// body is passing through the rock.
//
// Shared by the player's mantle and a mob's — which is the whole reason it is
// not a private helper on either. The same move must read the same way whoever
// makes it.
public static class LedgeCarry
{
    // Fraction of the traversal over which the LEADING axis completes.
    private const float LeadFraction = 0.6f;
    // Fraction of the traversal at which the TRAILING axis starts.
    private const float TrailStart = 0.25f;

    // Position at normalized time `t` (0 at the lip, 1 at the landing).
    public static Vector3 Position(Vector3 from, Vector3 to, float t)
    {
        float lead = Mathf.SmoothStep(0f, 1f, Mathf.Clamp(t / LeadFraction, 0f, 1f));
        float trail = Mathf.SmoothStep(0f, 1f, Mathf.Clamp((t - TrailStart) / (1f - TrailStart), 0f, 1f));

        bool descending = to.Y < from.Y;
        float vertT = descending ? trail : lead;
        float fwdT = descending ? lead : trail;

        return new Vector3(
            Mathf.Lerp(from.X, to.X, fwdT),
            Mathf.Lerp(from.Y, to.Y, vertT),
            Mathf.Lerp(from.Z, to.Z, fwdT));
    }
}
