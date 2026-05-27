using System;
using Godot;

// 3D-model counterpart to LitSpriteAnimator. Drives an AnimationPlayer that
// holds the combined player_anims library (clips named to match the
// EAnimation slot names authored on PlayerData), so the same state machine in
// Player.cs animates the skinned polyperfect mesh. Implements IActorAnimator
// so Player treats it interchangeably with the sprite animator.
//
// The "triggering approach" differs from LitSpriteAnimator: there are no
// per-frame texture swaps — Play() just hands the clip name to the
// AnimationPlayer, which blends and advances the skeleton. OnFrameAdvanced is
// synthesized from the continuous playback position (quantized at frameFps) so
// Player's frame-indexed footstep config still fires; cadence is approximate
// for the 3D clips and exact retiming is left as follow-up.
[GlobalClass]
public partial class ModelAnimator : Node, IActorAnimator
{
    [Export] public AnimationPlayer player;
    // Model root toggled with this animator's active state (the whole imported
    // character subtree). Hidden when the sprite visual is the active one.
    [Export] public Node3D visual;
    // Authored playback-rate multiplier (analogous to LitSpriteAnimator.speed).
    [Export] public float speed = 1f;
    // Nominal fps used to synthesize OnFrameAdvanced "frame" crossings from the
    // AnimationPlayer's continuous position. Matched to the sprite clips'
    // authored ~10fps so Player._footstepFrames indices land at similar
    // relative times. Approximate by design.
    [Export] public float frameFps = 10f;
    // Lit material applied to every MeshInstance3D under `visual` at _Ready.
    // The mesh lives inside the instanced FBX scene, so wiring material_override
    // per-surface in the .tscn would need editable-children; applying an
    // already-authored material in code (not creating one) is simpler and robust
    // to the imported mesh layout. Null leaves the imported materials in place.
    [Export] public ShaderMaterial modelMaterial;

    public float effectSpeedMultiplier { get; set; } = 1f;
    public StringName CurrentAnimation { get; private set; }
    public bool Finished { get; private set; }
    public event Action<StringName, int> OnFrameAdvanced;

    private int _lastFrame = -1;
    private bool _active;

    public override void _Ready()
    {
        if (player != null)
        {
            player.AnimationFinished += OnAnimationFinished;
        }
        if (modelMaterial != null && visual != null)
        {
            ApplyMaterial(visual);
        }
        // Default inactive until Player decides which visual is live.
        SetActive(_active);
    }

    // Override every surface of every MeshInstance3D in the subtree with the
    // lit material so the imported FBX materials don't render (they don't read
    // the world light map). material_override covers all surfaces of a mesh.
    private void ApplyMaterial(Node node)
    {
        if (node is MeshInstance3D mesh)
        {
            mesh.MaterialOverride = modelMaterial;
        }
        foreach (Node child in node.GetChildren())
        {
            ApplyMaterial(child);
        }
    }

    // Player calls this once in its _Ready when it selects the live visual.
    public void SetActive(bool active)
    {
        _active = active;
        if (visual != null)
        {
            visual.Visible = active;
        }
        SetProcess(active);
        if (!active && player != null)
        {
            player.Stop();
        }
    }

    public bool HasAnimation(StringName name)
    {
        return player != null && name != default && player.HasAnimation(name);
    }

    public void Play(StringName name)
    {
        // Mirror LitSpriteAnimator: re-playing the current still-running clip
        // is a no-op so a held loop isn't restarted every frame.
        if (CurrentAnimation == name && !Finished)
        {
            return;
        }
        if (player == null || !player.HasAnimation(name))
        {
            GD.PushError($"ModelAnimator '{Name}': unknown animation '{name}'");
            return;
        }
        CurrentAnimation = name;
        Finished = false;
        _lastFrame = 0;
        // Short cross-fade smooths the skeleton between state changes (the
        // sprite animator hard-cuts; a skinned mesh reads better blended).
        player.Play(name, customBlend: 0.12);
        player.SpeedScale = speed * effectSpeedMultiplier;
        OnFrameAdvanced?.Invoke(CurrentAnimation, 0);
    }

    public override void _Process(double delta)
    {
        if (player == null || CurrentAnimation == default)
        {
            return;
        }
        // Keep playback rate in lockstep with the per-frame status multiplier
        // Player writes (slow / haste), like LitSpriteAnimator folds it in.
        player.SpeedScale = speed * effectSpeedMultiplier;
        if (Finished)
        {
            return;
        }
        int frame = (int)(player.CurrentAnimationPosition * frameFps);
        if (frame == _lastFrame)
        {
            return;
        }
        if (frame > _lastFrame)
        {
            // Fire each crossed integer frame (matches LitSpriteAnimator so a
            // multi-frame step can't skip an authored footfall index).
            for (int f = _lastFrame + 1; f <= frame; f++)
            {
                OnFrameAdvanced?.Invoke(CurrentAnimation, f);
            }
        }
        else
        {
            // Looped/wrapped back to the start.
            OnFrameAdvanced?.Invoke(CurrentAnimation, frame);
        }
        _lastFrame = frame;
    }

    private void OnAnimationFinished(StringName animName)
    {
        if (animName == CurrentAnimation)
        {
            Finished = true;
        }
    }
}
