using System;
using System.Collections.Generic;
using Godot;

// Drives an AnimationPlayer that holds the combined player_anims library
// (clips named to match the EAnimation slot names authored on PlayerData), so
// the same state machine in Player.cs / Mob.cs animates the skinned polyperfect
// mesh.
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
public partial class ModelAnimator : Node
{
    [Export] public AnimationPlayer player;
    // Model root toggled with this animator's active state (the whole imported
    // character subtree).
    [Export] public Node3D visual;
    // Authored playback-rate multiplier.
    [Export] public float speed = 1f;
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
    // Optional second material for modular characters whose body and props are
    // separate meshes painted from DIFFERENT atlases (e.g. the goblin: one mesh
    // on the body texture, sword/shield/armor on a props texture). Meshes named
    // in secondaryMeshNames get this material; everything else gets
    // modelMaterial. Null (the common case — single-atlas characters like the
    // player and bunny) means every mesh gets modelMaterial.
    [Export] public ShaderMaterial secondaryMaterial;
    [Export] public string[] secondaryMeshNames = Array.Empty<string>();
    // Names of MeshInstance3D nodes under `visual` to hide at startup. Imported
    // character FBX often bundle alternate cosmetics (extra hairstyles, the
    // naked body underneath an outfit, optional helmets) that all render at
    // once unless culled. List the redundant ones here per scene.
    [Export] public string[] hiddenMeshNames = Array.Empty<string>();
    // Allowlist counterpart to hiddenMeshNames. When non-empty, ONLY the named
    // MeshInstance3D nodes stay visible and every other one under `visual` is
    // hidden — the natural shape for a modular character on a shared skeleton
    // (the All-in-One rig bundles every outfit's parts; an equipped loadout
    // names just the pieces it wants shown). Takes precedence over
    // hiddenMeshNames when both are set. Empty = fall back to the denylist.
    [Export] public string[] visibleMeshNames = Array.Empty<string>();

    // --- Modular-appearance mesh sets for the player rig ---
    // These name the rig's anatomy parts that the player's armor / appearance
    // compositor (PlayerArmorVisual) builds its visible set from. They live on
    // the rig because the names are gender-specific (the Female rig prefixes its
    // parts F_, the Male rig M_), so each gender's package scene authors its own.
    // Left empty on non-player rigs (mobs), which never run the compositor.

    // Always visible regardless of equipment: head shell + facial features.
    [Export] public string[] baseMeshNames = Array.Empty<string>();
    // Bare torso + legs shown when the body armor slot is empty.
    [Export] public string[] bareBodyMeshNames = Array.Empty<string>();
    // Skin meshes recolored by the chosen skin tone (face shell + bare body).
    [Export] public string[] skinMeshNames = Array.Empty<string>();
    // Hair-style menu: a creation-choice index (PlayerSpawnData.hairStyle) maps
    // to the hair MeshInstance3D name shown when no head armor is worn. Authored
    // in the SAME order across genders so a creation pick is gender-agnostic; an
    // empty / out-of-range pick resolves to bald (no hair mesh).
    [Export] public string[] hairStyleMeshNames = Array.Empty<string>();

    public float effectSpeedMultiplier { get; set; } = 1f;
    public StringName CurrentAnimation { get; private set; }
    public bool Finished { get; private set; }
    // Raised by EmitFootstep(), which a Call Method Track in the movement clips
    // invokes on the exact foot-contact frame. Player / Mob subscribe and run
    // their normal ground-resolution + footprint emission off it.
    public event Action OnFootstep;
    // Raised by EmitDigDirt(), invoked by a Call Method Track on the dig clip's
    // scoop frames so a dirt puff syncs to each shovel stroke. Player subscribes.
    public event Action OnDigDirt;

    private bool _active;
    // Accumulated real time waiting to be spent in discrete quantized steps.
    private double _stepAccum;
    // Cached refs for the facing pass: the camera (refreshed lazily) and the
    // body node the visual hangs under (its yaw is the "true" facing).
    private Camera3D _cachedCamera;
    private Node3D _body;
    // Every MeshInstance3D under `visual`, gathered once at _Ready. The
    // discovery presentation (visibility dither / silhouette / X-ray fade) is
    // pushed to these as per-instance shader params so undiscovered mobs dither
    // out — see SetDiscoveryVisuals, driven by Mob.cs.
    private readonly List<MeshInstance3D> _meshes = new();

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
        if (visual != null && (visibleMeshNames.Length > 0 || hiddenMeshNames.Length > 0))
        {
            ApplyMeshVisibility(visual);
        }
        _body = visual?.GetParent() as Node3D;
        if (visual != null)
        {
            CollectMeshes(visual);
        }
        // Default inactive until Player decides which visual is live.
        SetActive(_active);
    }

    private void CollectMeshes(Node node)
    {
        if (node is MeshInstance3D mesh)
        {
            _meshes.Add(mesh);
        }
        foreach (Node child in node.GetChildren())
        {
            CollectMeshes(child);
        }
    }

    // Push the discovery presentation onto every mesh of the model. visibility
    // drives the Bayer dither pop-in; silhouette blends to the flat memory tint;
    // xrayAmount fades the through-cover silhouette next_pass (1 = always-on when
    // occluded, as mobs want). castShadow is toggled per-mesh so a dithered-out
    // (undiscovered) mob also stops casting a tell-tale shadow.
    public void SetDiscoveryVisuals(float visibility, float silhouette, float xrayAmount, bool castShadow)
    {
        GeometryInstance3D.ShadowCastingSetting castMode = castShadow
            ? GeometryInstance3D.ShadowCastingSetting.On
            : GeometryInstance3D.ShadowCastingSetting.Off;
        for (int i = 0; i < _meshes.Count; i++)
        {
            MeshInstance3D mesh = _meshes[i];
            if (mesh == null)
            {
                continue;
            }
            mesh.SetInstanceShaderParameter("visibility", visibility);
            mesh.SetInstanceShaderParameter("silhouette_amount", silhouette);
            mesh.SetInstanceShaderParameter("xray_amount", xrayAmount);
            if (mesh.CastShadow != castMode)
            {
                mesh.CastShadow = castMode;
            }
        }
    }

    // Override every surface of every MeshInstance3D in the subtree with the
    // lit material so the imported FBX materials don't render (they don't read
    // the world light map). material_override covers all surfaces of a mesh.
    private void ApplyMaterial(Node node)
    {
        if (node is MeshInstance3D mesh)
        {
            bool secondary = secondaryMaterial != null
                && Array.IndexOf(secondaryMeshNames, mesh.Name.ToString()) >= 0;
            mesh.MaterialOverride = secondary ? secondaryMaterial : modelMaterial;
        }
        foreach (Node child in node.GetChildren())
        {
            ApplyMaterial(child);
        }
    }

    // Resolve each MeshInstance3D's visibility under `visual`. When
    // visibleMeshNames is set it's an allowlist (show only those, hide the
    // rest); otherwise hiddenMeshNames acts as a denylist. Bundled-but-unwanted
    // cosmetics (alternate hair, naked body under outfit, other outfits' parts)
    // are culled so they don't all render on top of each other.
    private void ApplyMeshVisibility(Node node)
    {
        if (node is MeshInstance3D mesh)
        {
            string name = mesh.Name.ToString();
            if (visibleMeshNames.Length > 0)
            {
                mesh.Visible = Array.IndexOf(visibleMeshNames, name) >= 0;
            }
            else if (Array.IndexOf(hiddenMeshNames, name) >= 0)
            {
                mesh.Visible = false;
            }
        }
        foreach (Node child in node.GetChildren())
        {
            ApplyMeshVisibility(child);
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

    // Pause/resume just the per-frame pose stepping (Advance + facing) WITHOUT
    // touching visibility or resetting the clip — for skipping the CPU skeletal
    // pose of mobs that aren't being rendered. The pose holds where it was;
    // resuming continues from there. No-op on an inactive animator.
    public void SetPoseProcessing(bool processing)
    {
        if (!_active)
        {
            return;
        }
        SetProcess(processing);
        // Stepped rigs (quantizeFps > 0) keep the AnimationPlayer paused and are
        // driven entirely by _Process, so toggling SetProcess is sufficient.
        // Smooth rigs let the player advance itself, so pause/resume it too.
        if (quantizeFps <= 0f && player != null && CurrentAnimation != default)
        {
            if (processing)
            {
                player.Play();
            }
            else
            {
                player.Pause();
            }
        }
    }

    // Runtime swap of the visible mesh set — the modular-armor hook. Replaces
    // visibleMeshNames and re-applies, so an equipped loadout can reveal/hide
    // outfit parts live on the one shared skeleton without reloading the model.
    public void SetVisibleMeshes(string[] names)
    {
        visibleMeshNames = names ?? Array.Empty<string>();
        if (visual != null)
        {
            ApplyMeshVisibility(visual);
        }
    }

    // Resolve the hair-style mesh name for a creation-menu index, or null (bald)
    // when out of range / unauthored — the visibility compositor treats null as
    // an empty bare-head set.
    public string GetHairStyleMesh(int index)
    {
        if (hairStyleMeshNames == null || index < 0 || index >= hairStyleMeshNames.Length)
        {
            return null;
        }
        return hairStyleMeshNames[index];
    }

    // Palette-recolor the named meshes to a flat tone via the `recolor` /
    // `recolor_amount` instance uniforms on model_lit (see the shader include).
    // Per-instance so skin meshes and the hair mesh can take different colors
    // while sharing the one model material; meshes not named here keep their
    // inert defaults (amount 0 = untouched). The player's modular-appearance
    // hook — skin tone on the body/face meshes, hair color on the live hair
    // style — applied once at spawn from Player. Names match the FBX node names.
    public void SetMeshRecolor(string[] meshNames, Color color)
    {
        SetMeshRecolor(meshNames, color, 1f);
    }

    // amount < 1 tints toward `color` while keeping the texture's albedo
    // variation (biome mob variants); amount 1 flat-replaces (player skin/hair).
    public void SetMeshRecolor(string[] meshNames, Color color, float amount)
    {
        if (meshNames == null || meshNames.Length == 0)
        {
            return;
        }
        // A vec3 source_color instance uniform takes a Vector3 (R,G,B), matching
        // SpriteBase's silhouette_tint push — not a Color Variant. Because we push
        // a Vector3 (not a Color), Godot does NOT apply the source_color sRGB->linear
        // conversion the hint implies, so we must do it here: the shader replaces the
        // (linear) sampled albedo with this value, and the palette is authored in
        // sRGB. Skipping this leaves skin/hair too bright + desaturated (washed
        // white, blooms in bright sun).
        Color linear = color.SrgbToLinear();
        Vector3 rgb = new(linear.R, linear.G, linear.B);
        for (int i = 0; i < _meshes.Count; i++)
        {
            MeshInstance3D mesh = _meshes[i];
            if (mesh == null)
            {
                continue;
            }
            if (Array.IndexOf(meshNames, mesh.Name.ToString()) >= 0)
            {
                mesh.SetInstanceShaderParameter("recolor", rgb);
                mesh.SetInstanceShaderParameter("recolor_amount", amount);
            }
        }
    }

    // Apply a mob's biome-variant palette at spawn. Null = leave the authored
    // textures untouched (the common case).
    public void ApplyPalette(MobPalette palette)
    {
        if (palette?.recolors == null)
        {
            return;
        }
        foreach (MobRecolorEntry entry in palette.recolors)
        {
            if (entry != null)
            {
                SetMeshRecolor(entry.meshNames, entry.color, entry.amount);
            }
        }
    }

    public bool HasAnimation(StringName name)
    {
        return player != null && name != default && player.HasAnimation(name);
    }

    // `restart` forces a re-fire of the SAME clip to replay from the start
    // (one-shot attacks, hitstun, jump): mashing the knife's light attack maps
    // to the same `stab1` slot every press and must interrupt itself. The
    // looping locomotion pick leaves restart false so a held run/idle loop
    // isn't yanked back to frame 0 every frame.
    public void Play(StringName name, bool restart = false)
    {
        // Mirror LitSpriteAnimator: re-playing the current still-running clip
        // is a no-op so a held loop isn't restarted every frame.
        if (CurrentAnimation == name && !Finished && !restart)
        {
            return;
        }
        if (player == null || !player.HasAnimation(name))
        {
            GD.PushError($"ModelAnimator '{Name}': unknown animation '{name}'");
            return;
        }
        bool sameClip = CurrentAnimation == name;
        CurrentAnimation = name;
        Finished = false;
        _stepAccum = 0.0;
        // Stepped mode hard-cuts (no cross-fade) to keep the stop-motion read;
        // smooth mode uses a short blend so state changes don't pop.
        player.Play(name, customBlend: quantizeFps > 0f ? 0.0 : 0.12);
        // Godot's AnimationPlayer.Play() continues (doesn't rewind) when the
        // clip is already current, so a same-clip restart must seek to 0
        // explicitly. A different clip already starts at 0 — leave its blend-in
        // alone.
        if (restart && sameClip)
        {
            player.Seek(0.0, update: true);
        }
        if (quantizeFps > 0f)
        {
            // Pause auto-advance — _Process drives the position in discrete
            // steps via Advance(); Godot still handles loop wrap inside Advance.
            player.Pause();
        }
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
    }

    // Call Method Track target. Authored as a key on the foot-contact frame of
    // each movement clip (run / sprint / sneak) in the character's
    // AnimationLibrary; the path the track stores is relative to the
    // AnimationPlayer's root, pointing at this ModelAnimator node. Raises
    // OnFootstep so the subscribed Player / Mob emits the footstep + footprint
    // exactly when the foot plants. Public so Godot's animation system can
    // invoke it by name. No-op unless something is listening.
    public void EmitFootstep()
    {
        OnFootstep?.Invoke();
    }

    // Call Method Track target authored on the dig clip's scoop frames (see
    // PlayerAnimManifest's "digging" row). Raises OnDigDirt so the subscribed
    // Player spawns a dirt puff exactly as the blade bites in. Public so Godot's
    // animation system can invoke it by name; no-op unless something listens.
    public void EmitDigDirt()
    {
        OnDigDirt?.Invoke();
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
