using Godot;

// The traversal classes ledge barriers are built for.
//
// A barrier walls every drop a body must never cross ACCIDENTALLY — deeper than
// its own maxFallHeight. That threshold is per-body, and a barrier is baked
// geometry, so there is one mesh per distinct threshold and a body masks in the
// one matching its own. Being geometry is the whole point: the body is stopped
// by a wall with true normals and slides along it, instead of a per-tick probe
// guessing at a lookahead distance and faking the slide by axis.
//
// The set is enumerated rather than derived, which makes maxFallHeight a CLASS
// rather than a free number. That is an honest constraint, not a shortcut: a
// threshold that has to be baked into world geometry cannot be a per-mob dial,
// and the three values here are every one the game actually authors (the player
// never falls on purpose, a goblin only descends what it could climb, an
// ordinary mob takes a chase drop).
public static class LedgeBarrierClasses
{
    public readonly struct Entry
    {
        // Deepest drop a body of this class accepts. The barrier stands at the
        // top of everything deeper.
        public readonly int MaxFallVoxels;
        public readonly ECollisionLayer Layer;

        public Entry(int maxFallVoxels, ECollisionLayer layer)
        {
            MaxFallVoxels = maxFallVoxels;
            Layer = layer;
        }
    }

    // Ordered shallowest first — LayerFor relies on it.
    public static readonly Entry[] All =
    {
        new(1, ECollisionLayer.LedgeBarrierFall1),
        new(2, ECollisionLayer.LedgeBarrierFall2),
        new(4, ECollisionLayer.LedgeBarrierFall4),
    };

    private static bool _warned;

    // The barrier a body with this maxFallHeight should collide with.
    //
    // An unlisted value falls back to the deepest class that is still no
    // stricter than the body — permissive rather than strict, because a barrier
    // stricter than the body's own rule wedges it at a drop its router
    // deliberately routed, and a wedged mob is far worse than one that
    // occasionally takes a drop a voxel deeper than authored. Warns once so the
    // authoring gets fixed rather than silently tolerated.
    public static ECollisionLayer LayerFor(int maxFallHeight)
    {
        Entry best = All[0];
        bool exact = false;
        for (int i = 0; i < All.Length; i++)
        {
            if (All[i].MaxFallVoxels == maxFallHeight)
            {
                return All[i].Layer;
            }
            if (All[i].MaxFallVoxels <= maxFallHeight)
            {
                best = All[i];
            }
        }
        if (!exact && !_warned)
        {
            _warned = true;
            GD.PushWarning($"[ledge_barrier] maxFallHeight {maxFallHeight} is not a barrier class "
                + $"({string.Join(", ", System.Array.ConvertAll(All, e => e.MaxFallVoxels))}); "
                + $"using the {best.MaxFallVoxels} barrier. Author one of the classes.");
        }
        return best.Layer;
    }
}
