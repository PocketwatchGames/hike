using Godot;

// Per-animation list of frame indices where the foot strikes the ground.
// Authored as entries in Player / Mob's `_footstepFrames` array; the
// animator fires OnFrameAdvanced as the sprite cycles, and a matching
// (anim, frame) pair triggers a footstep + footprint.
[GlobalClass]
public partial class FootstepFrameSet : Resource
{
    [Export] public StringName anim;
    [Export] public Godot.Collections.Array<int> frames = new();

    // Linear scan over an actor's authored sets. Lists are tiny (one entry
    // per movement animation, typically 1–3 total), so this runs in
    // O(setCount × framesPerSet) once per frame advance — negligible.
    public static bool Matches(Godot.Collections.Array<FootstepFrameSet> sets, StringName anim, int frame)
    {
        if (sets == null)
        {
            return false;
        }
        for (int i = 0; i < sets.Count; i++)
        {
            FootstepFrameSet set = sets[i];
            if (set == null || set.anim != anim || set.frames == null)
            {
                continue;
            }
            if (set.frames.Contains(frame))
            {
                return true;
            }
        }
        return false;
    }
}
