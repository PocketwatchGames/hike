using System;
using Godot;

// 3D-model counterpart to LitSpriteAnimator. Drives an AnimationPlayer that
// holds the combined player_anims library (clips named to match the
// EAnimation slot names authored on PlayerData), so the same state machine in
// Player.cs animates the skinned polyperfect mesh. Implements IActorAnimator
// so Player treats it interchangeably with the sprite animator.
//
// Beyond playing clips it adds two stylization passes that make the 3D model
// read like the game's pixel-art sprites:
//   - Stepped sampling: the skeleton pose only changes at a fixed low rate
//     (quantizeFps, default 12) instead of the render frame rate, for a
//     stop-motion / hand-animated cadence. The AnimationPlayer is paused and
//     driven manually with Advance() in discrete 1/quantizeFps steps, so
//     Godot still owns looping (a manual Seek backward across the loop seam
//     caused a one-frame bind-pose flash; Advance wraps cleanly).
//   - Faceting: the visual's yaw is snapped to N directions (default 8)
//     measured relative to the camera, mimicking 8-directional sprite art.
//     Only the VISUAL is faceted — the Player body keeps its smooth yaw for
//     aiming / movement / firing.
[GlobalClass]
public partial class ModelAnimator : Node, IActorAnimator
{
    [Export] public AnimationPlayer player;
    // Model root toggled with this animator's active state (the whole imported
    // character subtree). Hidden when the sprite visual is the active one.
    [Export] public Node3D visual;
    // Authored playback-rate multiplier (analogous to LitSpriteAnimator.speed).
    [Export] public float speed = 1f;
    // Nominal fps used to synthesize OnFrameAdvanced "frame" crossings for
    // Player._footstepFrames. Matched to the sprite clips' authored ~10fps so
    // footfall frame indices land at similar relative times. Approximate.
    [Export] public float frameFps = 10f;
    // Pose sampling rate (stop-motion look). The skeleton is only re-posed this
    // many times per second. <= 0 disables the effect and plays back smoothly
    // via the AnimationPlayer's own advance.
    [Export] public float quantizeFps = 12f;
    // Number of yaw facings the visual snaps to, measured relative to the
    // camera (8 = 45 degrees apart, like 8-directional sprite art). <= 1
    // disables faceting and the model inherits the body's smooth yaw.
    [Export] public int facingDirections = 8;
    // Lit material applied to every MeshInstance3D under `visual` at _Ready.
    // The mesh lives inside the instanced FBX scene, so wiring material_override
    // per-surface in the .tscn would need editable-children; applying an
    // already-authored material in code (not creating one) is simpler and robust
    // to the imported mesh layout. Null leaves the imported materials in place.
    [Export] public ShaderMaterial modelMaterial;
    // Names of MeshInstance3D nodes under `visual` to hide at startup. Imported
    // character FBX often bundle alternate cosmetics (extra hairstyles, the
    // naked body underneath an outfit, optional helmets) that all render at
    // once unless culled. List the redundant ones here per scene.
    [Export] public string[] hiddenMeshNames = Array.Empty<string>();

    public float effectSpeedMultiplier { get; set; } = 1f;
    public StringName CurrentAnimation { get; private set; }
    public bool Finished { get; private set; }
    public event Action<StringName, int> OnFrameAdvanced;

    private int _lastFrame = -1;
    private bool _active;
    // Accumulated real time waiting to be spent in discrete quantized steps.
    private double _stepAccum;
    // Cached refs for the facing pass: the camera (refreshed lazily) and the
    // body node the visual hangs under (its yaw is the "true" facing).
    private Camera3D _cachedCamera;
    private Node3D _body;

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
        if (hiddenMeshNames.Length > 0 && visual != null)
        {
            HideMeshes(visual);
        }
        _body = visual?.GetParent() as Node3D;
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

    // Cull MeshInstance3D nodes named in `hiddenMeshNames` so bundled-but-
    // unwanted cosmetics (alternate hair, naked body under outfit, etc.) don't
    // all render on top of each other.
    private void HideMeshes(Node node)
    {
        if (node is MeshInstance3D mesh && Array.IndexOf(hiddenMeshNames, mesh.Name.ToString()) >= 0)
        {
            mesh.Visible = false;
        }
        foreach (Node child in node.GetChildren())
        {
            HideMeshes(child);
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
        _stepAccum = 0.0;
        // Stepped mode hard-cuts (no cross-fade) to keep the stop-motion read;
        // smooth mode uses a short blend so state changes don't pop.
        player.Play(name, customBlend: quantizeFps > 0f ? 0.0 : 0.12);
        if (quantizeFps > 0f)
        {
            // Pause auto-advance — _Process drives the position in discrete
            // steps via Advance(); Godot still handles loop wrap inside Advance.
            player.Pause();
        }
        OnFrameAdvanced?.Invoke(CurrentAnimation, 0);
    }

    public override void _Process(double delta)
    {
        UpdateFacing();

        if (player == null || CurrentAnimation == default)
        {
            return;
        }

        if (quantizeFps > 0f)
        {
            // Stepped: the player is paused; spend accumulated time in fixed
            // 1/quantizeFps chunks so the pose only changes at those instants.
            // Advance() handles looping/finish natively (no backward seek).
            _stepAccum += delta * speed * effectSpeedMultiplier;
            double step = 1.0 / quantizeFps;
            while (!Finished && _stepAccum >= step)
            {
                player.Advance(step);
                _stepAccum -= step;
            }
        }
        else
        {
            // Smooth: let the AnimationPlayer advance itself.
            player.SpeedScale = speed * effectSpeedMultiplier;
        }

        if (!Finished)
        {
            EmitFrameEvents(player.CurrentAnimationPosition);
        }
    }

    // Synthesize OnFrameAdvanced "frame" crossings from a continuous position so
    // Player._footstepFrames (authored as sprite-frame indices) still triggers.
    private void EmitFrameEvents(double pos)
    {
        int frame = (int)(pos * frameFps);
        if (frame == _lastFrame)
        {
            return;
        }
        if (frame > _lastFrame)
        {
            for (int f = _lastFrame + 1; f <= frame; f++)
            {
                OnFrameAdvanced?.Invoke(CurrentAnimation, f);
            }
        }
        else
        {
            // Looped/wrapped back toward the start.
            OnFrameAdvanced?.Invoke(CurrentAnimation, frame);
        }
        _lastFrame = frame;
    }

    // Snap the visual's yaw to one of `facingDirections` headings measured
    // relative to the camera, so the model reads like 8-directional sprite art.
    // The body (visual's parent) keeps its smooth gameplay yaw; we only adjust
    // the visual's LOCAL yaw so its WORLD yaw lands on a facet boundary.
    private void UpdateFacing()
    {
        if (facingDirections < 2 || visual == null || _body == null)
        {
            return;
        }
        if (_cachedCamera == null || !IsInstanceValid(_cachedCamera))
        {
            _cachedCamera = visual.GetViewport()?.GetCamera3D();
            if (_cachedCamera == null)
            {
                return;
            }
        }
        // Atan2(x, z) convention matches Player's yaw assignment, so body and
        // camera yaws are compared in the same frame of reference.
        float bodyYaw = _body.GlobalRotation.Y;
        Vector3 camFwd = -_cachedCamera.GlobalBasis.Z;
        float camYaw = Mathf.Atan2(camFwd.X, camFwd.Z);
        float step = Mathf.Tau / facingDirections;
        float snapped = Mathf.Round((bodyYaw - camYaw) / step) * step;
        float worldYaw = camYaw + snapped;
        Vector3 rot = visual.Rotation;
        rot.Y = worldYaw - bodyYaw;
        visual.Rotation = rot;
    }

    private void OnAnimationFinished(StringName animName)
    {
        if (animName == CurrentAnimation)
        {
            Finished = true;
        }
    }
}
