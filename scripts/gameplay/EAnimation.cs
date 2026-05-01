using Godot;

// Canonical animation identifiers. Author-facing channels (action timeline
// ItemEvent.animName, scene SpriteFrames clip names) stay StringName-typed
// so designers can spell anims directly in .tres files; in-engine code
// goes through this enum to avoid typos and to pick up rename refactors.
public enum EAnimation
{
    Attack,
    Dead,
    Die,
    Fall,
    Idle,
    Jump,
    Run,
    Swim,
    SwimIdle,
}

public static class AnimationNames
{
    // Cached per-enum StringNames. StringName equality is interned, but the
    // C# `"foo"` → StringName implicit conversion still allocates a wrapper
    // each call — caching once keeps Play / HasAnimation lookups allocation
    // free at the call site.
    public static readonly StringName Attack = "attack";
    public static readonly StringName Dead = "dead";
    public static readonly StringName Die = "die";
    public static readonly StringName Fall = "fall";
    public static readonly StringName Idle = "idle";
    public static readonly StringName Jump = "jump";
    public static readonly StringName Run = "run";
    public static readonly StringName Swim = "swim";
    public static readonly StringName SwimIdle = "swim_idle";

    // Indexed by EAnimation cast to int — declaration order in EAnimation
    // and the array MUST match. Add new entries at the bottom of both.
    static readonly StringName[] _byEnum = new[]
    {
        Attack, Dead, Die, Fall, Idle, Jump, Run, Swim, SwimIdle,
    };

    public static StringName Get(EAnimation anim) => _byEnum[(int)anim];
}
