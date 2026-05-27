using System;
using Godot;

// Animation-driver abstraction shared by the sprite player
// (LitSpriteAnimator) and the 3D-model player (ModelAnimator). Player.cs
// drives whichever visual is active through this interface, so the single
// EAnimation state machine animates either the billboard sprite or the
// skinned mesh without branching at every call site. The members are exactly
// what Player consumes from its animator (Play / HasAnimation / state reads /
// the footstep frame event / the status-driven speed multiplier). Both
// implementors are Godot Nodes, so Player exports the concrete node type and
// resolves the interface in _Ready.
public interface IActorAnimator
{
    // Fired when the playing animation advances to a new frame index. Player
    // subscribes to drive footstep / footprint emission off animation cadence.
    event Action<StringName, int> OnFrameAdvanced;

    // Currently-playing clip name (the StringName resolved from EAnimation via
    // PlayerData.GetAnimationName).
    StringName CurrentAnimation { get; }

    // True once a non-looping clip has played to its last frame.
    bool Finished { get; }

    // Runtime speed modulator multiplied into authored playback speed each
    // tick (slow / haste status effects). Owners reset it from their motion
    // update; see AnimationData.affectedBySpeedMultiplier.
    float effectSpeedMultiplier { get; set; }

    bool HasAnimation(StringName name);
    void Play(StringName name);
}
