using System;
using System.Collections.Generic;
using Godot;

// Composable perception slot for interactives. Drop a Discoverable child
// into any interactive scene that should be perception-gated (spike trap,
// chest, secret door); leave it out for interactives that are always
// visible (regular door, torch). Holds the per-instance perception
// tunings, runs the throttled tick, and fires OnStateChanged when the
// host should refresh visuals or interact-enabled.
//
// Discovery is permanent — once a host hits Discovered, the helper stops
// re-evaluating. Hosts that want the suspicious-phase HUD callout (trap,
// secret passage) wire HudScene AND keep detectedThreshold below
// discoveredThreshold so the Detected state is reachable. Hosts that
// should pop directly to visible (chest) leave HudScene null and set
// detectedThreshold == discoveredThreshold so Detected is skipped.
[GlobalClass]
public partial class Discoverable : Node3D
{
    // Free scalar on the player's visionRange — bump above 1 for large /
    // conspicuous targets (chests, big secret doors) so they cross the
    // perception threshold from farther away. Default 1 = same reach as
    // the player's base vision.
    [Export] public float prominence = 1f;
    // Perception value at which the host transitions Hidden → Detected.
    // Set equal to discoveredThreshold to skip the Detected phase
    // entirely (chests pop straight to visible with no HUD beat).
    [Export(PropertyHint.Range, "0,1,0.01")] public float detectedThreshold = 0.1f;
    // Perception value at which the host transitions to Discovered.
    // Almost always 1.0; lower values let unmissable targets pop early.
    [Export(PropertyHint.Range, "0,1,0.01")] public float discoveredThreshold = 1f;
    // Height above origin to sample world light at. ~0.5m for a floor pad
    // (avoids the floor voxel zeroing the sample); ~1m for a wall-mounted
    // target.
    [Export] public float lightSampleHeight = 0.5f;
    // Height above origin used as the LOS raycast endpoint. 0 for a floor
    // pad, ~1.5m for a chest or door so a low wall in front breaks LOS.
    [Export] public float losRayHeight = 1f;
    // Skip the LOS raycast entirely. Footprints set this true: light already
    // gates "can't see in the dark / behind a wall," and at footprint
    // density the per-tick raycast cost across many decals dominates.
    [Export] public bool skipLineOfSightCheck = false;

    // Optional worldspace HUD shown during Detected. Null = no callout.
    [Export] public PackedScene hudScene;
    // Screen-space scale applied to the spawned HUD. Bump down for small
    // targets (footprints) or up for large ones; mirrors MobData.hudScale.
    [Export] public float hudScale = 1f;
    [Export] private Node3D _hudAnchor;
    // SpriteBase subclasses (LitSprite / FlatLitSprite) that should dither
    // in / out as discovery state changes. Wire any sprite that should
    // fade rather than hard-pop (chest body, chest open variant, secret-
    // door panel, spike-trap holes). Plain Sprite3D nodes can't dither —
    // only the custom shader carries the Visibility uniform — so
    // authoring this list requires the host's sprites to derive from
    // SpriteBase. Hosts whose sprites are already always-visible (regular
    // doors, torches) leave this empty.
    [Export] private Godot.Collections.Array<SpriteBase> _fadeSprites = new();
    // 3D-mesh counterpart of _fadeSprites for mesh-based hosts (chests, the
    // statue, mesh secret doors). Every MeshInstance3D under this node dithers
    // in/out with discovery via the `visibility` instance uniform that
    // model_lit carries — the same Bayer fade the model mobs use — instead of
    // hard-popping. Leave null on sprite-only hosts. The meshes must use a
    // model_lit-family material (it declares the uniform); a mesh on some other
    // shader silently won't fade. Cast shadow is suppressed while fully faded
    // out so an undiscovered host casts no tell-tale shadow.
    [Export] private Node3D _fadeMeshRoot;
    // Optional InteractiveBox (or any Area3D) the player's interactArea
    // picks up. Toggled off until Discovered so a not-yet-noticed chest /
    // secret door doesn't draw an interact prompt. Hosts with extra
    // gating beyond perception (the spike trap, which also gates on
    // trap-state == Armed) leave this unset and manage their own
    // InteractiveBox lifecycle.
    [Export] private Area3D _interactBox;

    public Action<EPlayerPerceptionState> OnStateChanged;

    // Seconds for the discovery fade to traverse 0..1. Slightly longer than
    // Mob's 0.3s so a freshly-spawned chest reads as "you noticed it" rather
    // than "it teleported in."
    private const float FadeTime = 0.4f;
    private float _visibility;

    private PerceivedByPlayerState _state;
    private World _world;
    // MeshInstance3D descendants of _fadeMeshRoot, gathered once at _Ready.
    private readonly List<MeshInstance3D> _fadeMeshes = new();

    public EPlayerPerceptionState State => _state.state;
    public float Perception => _state.perception;
    public bool IsDiscovered => _state.state == EPlayerPerceptionState.Discovered;
    public bool IsDetected => _state.state == EPlayerPerceptionState.Detected;
    public Vector3 HudPosition => _hudAnchor != null ? _hudAnchor.GlobalPosition : GlobalPosition;
    public float PerceptionProgress
    {
        get
        {
            // Guard against detected==discovered (chest case). The HUD
            // doesn't render in that case anyway, but division by zero
            // would still leak a NaN through any debug readout.
            float span = Mathf.Max(0.0001f, discoveredThreshold - detectedThreshold);
            return Mathf.Clamp((_state.perception - detectedThreshold) / span, 0f, 1f);
        }
    }

    public override void _Ready()
    {
        _state.tickAccumulator = (float)GD.RandRange(0.0, PlayerPerception.TickInterval);
        _world = World.Current;
        if (_world != null)
        {
            _world.onDiscoverableSpawned?.Invoke(this);
            TreeExiting += () => _world.onDiscoverableRemoved?.Invoke(this);
        }
        if (_fadeMeshRoot != null)
        {
            CollectFadeMeshes(_fadeMeshRoot);
        }
        // Seed the dither uniform on every wired sprite/mesh so a pre-Discovered
        // host doesn't render at the default Visibility=1 for a frame before the
        // first _Process tick takes over.
        PushFade();
        // Initial interact gate matches Hidden state — disabled until the
        // host hits Discovered.
        ApplyInteractGate();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_state.state == EPlayerPerceptionState.Discovered)
        {
            return;
        }

        _state.tickAccumulator += (float)delta;
        if (_state.tickAccumulator < PlayerPerception.TickInterval)
        {
            return;
        }

        float tickDelta = _state.tickAccumulator;
        _state.tickAccumulator = 0f;

        var inputs = new PerceptionInputs
        {
            prominence = prominence,
            rangeScale = 1f,
            detectedThreshold = detectedThreshold,
            discoveredThreshold = discoveredThreshold,
            lightSampleHeight = lightSampleHeight,
            losRayHeight = losRayHeight,
            skipLineOfSight = skipLineOfSightCheck,
        };
        PerceptionTickResult result = PlayerPerception.Tick(_world, GlobalPosition, in inputs, ref _state, tickDelta, out _);
        if (result.stateChanged)
        {
            ApplyInteractGate();
            OnStateChanged?.Invoke(_state.state);
        }
    }

    private void ApplyInteractGate()
    {
        if (_interactBox == null)
        {
            return;
        }
        bool enabled = IsDiscovered;
        _interactBox.Monitorable = enabled;
        _interactBox.Monitoring = enabled;
    }

    public override void _Process(double delta)
    {
        // Discovered is terminal, so target is 1; pre-Discovered targets
        // ride at 0 (Detected included — the suspicious-phase HUD is the
        // callout there, the sprite stays dithered out). Once _visibility
        // reaches the target the lerp short-circuits and we stop pushing
        // the uniform every frame.
        float target = _state.state == EPlayerPerceptionState.Discovered ? 1f : 0f;
        if (_visibility == target)
        {
            return;
        }
        float step = (float)delta / FadeTime;
        _visibility = Mathf.MoveToward(_visibility, target, step);
        PushFade();
    }

    private void CollectFadeMeshes(Node node)
    {
        if (node is MeshInstance3D mesh)
        {
            _fadeMeshes.Add(mesh);
        }
        foreach (Node child in node.GetChildren())
        {
            CollectFadeMeshes(child);
        }
    }

    private void PushFade()
    {
        if (_fadeSprites != null)
        {
            for (int i = 0; i < _fadeSprites.Count; i++)
            {
                SpriteBase sprite = _fadeSprites[i];
                if (sprite != null)
                {
                    sprite.Visibility = _visibility;
                }
            }
        }
        // Mesh hosts: push the dither uniform per instance (so meshes sharing a
        // material still fade independently) and drop cast-shadow while fully
        // faded out, matching the model-mob path in ModelAnimator.
        GeometryInstance3D.ShadowCastingSetting castMode = _visibility > 0f
            ? GeometryInstance3D.ShadowCastingSetting.On
            : GeometryInstance3D.ShadowCastingSetting.Off;
        for (int i = 0; i < _fadeMeshes.Count; i++)
        {
            MeshInstance3D mesh = _fadeMeshes[i];
            if (mesh == null)
            {
                continue;
            }
            mesh.SetInstanceShaderParameter("visibility", _visibility);
            if (mesh.CastShadow != castMode)
            {
                mesh.CastShadow = castMode;
            }
        }
    }

    // Force-promote to Discovered. Used when discovery is implied by event
    // (a spike trap that just emerged spikes; a secret door the player
    // bumped into) regardless of whether perception had built up enough.
    public void ForceDiscover()
    {
        EPlayerPerceptionState prev = _state.state;
        PlayerPerception.ForceDiscover(ref _state);
        if (_state.state != prev)
        {
            ApplyInteractGate();
            OnStateChanged?.Invoke(_state.state);
        }
    }

    // Restore prior perception state on chunk reload. Currently called
    // from the Discovered branch of any host that persists discovery —
    // hosts read their own boolean from sim state, then re-promote on
    // spawn so the saved-then-reloaded chest stays visible.
    public void RestoreDiscovered()
    {
        ForceDiscover();
    }
}
