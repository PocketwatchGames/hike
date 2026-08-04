using System.Collections.Generic;
using Godot;

// Probes player visibility on a coarse cadence and fades the X-ray pass on
// a set of LitSprites AND/OR 3D meshes so an interactive only silhouettes
// through walls when the player is roughly looking at it. Player + mobs use the
// X-ray pass at full strength all the time (every threat is gameplay-relevant);
// broadcasting every chest, door, and torch in the world the same way would be
// visual noise, hence the per-instance probe + fade.
//
// Two render primitives, one driver: billboards (`_sprites`, SpriteBase whose
// material is sprite_lit_character) and mesh interactives (`_meshRoots`, model
// meshes whose material is model_lit_character + a model_xray next_pass — chest,
// stash, campfire). Both expose an `xray_amount` per-instance fade the probe
// drives 0→1→0; this class pushes the same value to whichever set is wired.
//
// Wiring: drop one of these as a child of an interactive scene. Point `_sprites`
// at the SpriteBase subclass(es) and/or `_meshRoots` at the model container
// node(s) whose MeshInstance3D descendants should X-ray, and optionally bind
// `_discoverable` so a state change forces the X-ray on instantly (the chest
// pops into existence and the silhouette is already there instead of waiting up
// to one probe interval to confirm).
//
// Probe model: one cadence (~0.5s, jittered per-instance), range-gated so
// far-away interactives skip the raycast entirely. Asymmetric lerp — fast
// in (snaps on when the player rounds a corner) and slow out (a brief LOS
// blink behind a tree doesn't kill the silhouette).
[GlobalClass]
public partial class InteractiveXray : Node3D
{
    [Export] public float xrayRange = 12f;
    [Export] public float probeInterval = 0.5f;
    [Export] public float fadeInRate = 10f;
    [Export] public float fadeOutRate = 2f;
    // Height above origin used as the LOS raycast endpoint, mirroring
    // Discoverable.losRayHeight — a chest probe should clear a low wall
    // the same way the perception probe does.
    [Export] public float losRayHeight = 1f;
    // Maximum |player_eye_y - interactive_y| (meters) for the X-ray to
    // fire. Without this gate a chest on the ground floor would silhouette
    // through the floor while the player walks the second story above, or
    // through the ceiling while they're in a basement below — confusing
    // since they're not actually "behind cover" relative to the chest,
    // they're on a different plateau entirely. Symmetric absolute compare
    // (so wall-mounted torches and floor chests are handled the same way),
    // default 3m covers typical voxel floor-to-floor spacing with slop.
    // Bump higher for tall interactives or open verticality; tighter for
    // dense multi-story interiors where you want stricter elevation gating.
    [Export] public float plateauHeight = 3f;
    [Export] private Godot.Collections.Array<SpriteBase> _sprites = new();
    // Model-mesh interactives (chest, stash, campfire): point these at the
    // container node(s) holding the instanced FBX. Every MeshInstance3D
    // descendant is gathered once and gets the same xray_amount push the
    // sprites do. The meshes' material must be model_lit_character with a
    // model_xray next_pass for the fade to render (see chest_interactive.tres).
    [Export] private Godot.Collections.Array<Node3D> _meshRoots = new();
    private readonly List<MeshInstance3D> _meshes = new();
    private static readonly StringName XrayAmountParam = "xray_amount";
    // Optional: when set, the X-ray snaps on the moment the host's perception
    // state changes (e.g. chest goes Hidden → Discovered). Without this kick
    // the X-ray would wait up to probeInterval before catching up.
    [Export] private Discoverable _discoverable;

    private const float PlayerEyeHeight = 1.5f;

    private float _xrayAmount;
    private float _xrayTarget;
    private float _probeAccumulator;
    // Host that the X-ray follows, resolved from GetParent() in _Ready. When
    // the host reports ShouldShowXray() == false (chest opened, loot picked
    // up), the probe is suppressed and the silhouette fades out. Null means
    // "no host found" — the xray then runs unconditionally.
    private IInteractive _interactive;

    public override void _Ready()
    {
        // Stagger so a chunk full of interactives doesn't raycast on the
        // same frame. RandRange seeds the accumulator anywhere in [0, 1)
        // probe intervals so the first probe lands on a random frame
        // within the first probeInterval seconds of life.
        _probeAccumulator = (float)GD.RandRange(0.0, probeInterval);
        if (_discoverable != null)
        {
            _discoverable.OnStateChanged += OnDiscoverableStateChanged;
        }
        _interactive = GetParent() as IInteractive;
        foreach (Node3D root in _meshRoots)
        {
            if (root != null)
            {
                CollectMeshes(root);
            }
        }
        ApplyXrayAmount(0f);
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

    private void OnDiscoverableStateChanged(EPlayerPerceptionState state)
    {
        // Force a probe next physics frame and pre-seed the target. The
        // probe will confirm or revert; this just removes the up-to-0.5s
        // wait between "chest pops visible" and "silhouette starts fading
        // in." If LOS is genuinely blocked the very next probe drops the
        // target back to 0 and the slow fade-out absorbs the brief spike.
        _xrayTarget = 1f;
        _probeAccumulator = probeInterval;
    }

    public override void _PhysicsProcess(double delta)
    {
        using var _prof = Profiler.Sample("InteractiveXray.PhysicsProcess");

        // Skip everything while a host Discoverable is still pre-Discovered:
        // the sprite is hidden, so the X-ray pass has no visible effect and
        // the raycast / uniform push are pure waste. The OnStateChanged
        // hook above kicks _xrayTarget = 1 the moment the host transitions,
        // and the next physics tick from here picks up the probe.
        if (_discoverable != null && !_discoverable.IsDiscovered)
        {
            return;
        }

        float dt = (float)delta;
        // Skip the LOS probe (and force the target to 0) when the host no longer
        // wants the discovery silhouette — an opened chest, picked-up loot, or a
        // stash the player has already found. Falls through to the fade path
        // below so the silhouette dims away naturally instead of snapping off.
        bool shouldXray = _interactive == null || _interactive.ShouldShowXray();
        if (shouldXray)
        {
            _probeAccumulator += dt;
            if (_probeAccumulator >= probeInterval)
            {
                _probeAccumulator = 0f;
                _xrayTarget = Probe();
            }
        }
        else
        {
            _xrayTarget = 0f;
        }

        if (_xrayAmount == _xrayTarget)
        {
            return;
        }

        // Asymmetric exponential decay. Independent of physics step rate and
        // framerate-stable. fadeInRate higher = snaps on faster; fadeOutRate
        // lower = lingers longer after LOS is lost.
        float rate = _xrayTarget > _xrayAmount ? fadeInRate : fadeOutRate;
        float blend = 1f - Mathf.Exp(-rate * dt);
        float next = Mathf.Lerp(_xrayAmount, _xrayTarget, blend);
        // Snap to the target once we're below visible-precision so the
        // sprite uniform stops getting pushed every physics tick forever.
        if (Mathf.Abs(next - _xrayTarget) < 1e-3f)
        {
            next = _xrayTarget;
        }
        ApplyXrayAmount(next);
    }

    private void ApplyXrayAmount(float value)
    {
        _xrayAmount = value;
        foreach (SpriteBase sprite in _sprites)
        {
            if (sprite != null)
            {
                sprite.XrayAmount = value;
            }
        }
        foreach (MeshInstance3D mesh in _meshes)
        {
            if (mesh != null)
            {
                mesh.SetInstanceShaderParameter(XrayAmountParam, value);
            }
        }
    }

    // Range gate first (squared comparison, no sqrt) so far interactives
    // pay the cheapest possible "no" and skip the raycast.
    private float Probe()
    {
        Sim sim = Sim.Current;
        if (sim == null || sim.player == null)
        {
            return 0f;
        }
        Player player = sim.player;
        Vector3 origin = GlobalPosition;
        float distSq = (origin - player.GlobalPosition).LengthSquared();
        if (distSq > xrayRange * xrayRange)
        {
            return 0f;
        }
        // Plateau-elevation gate, run before the raycast so cross-plateau
        // probes pay nothing. Compares the player's eye Y against the
        // interactive's anchor Y; a basement chest while the player walks
        // the floor above falls outside the band and skips X-ray.
        float playerEyeY = player.GlobalPosition.Y + PlayerEyeHeight;
        if (Mathf.Abs(playerEyeY - origin.Y) > plateauHeight)
        {
            return 0f;
        }

        Vector3 rayStart = origin + new Vector3(0f, losRayHeight, 0f);
        Vector3 rayEnd = player.GlobalPosition + new Vector3(0f, PlayerEyeHeight, 0f);
        using var query = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd, (uint)ECollisionLayer.Solid);
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        Godot.Collections.Dictionary rayResult = player.GetWorld3D().DirectSpaceState.IntersectRay(query);
        // LOS clear → broadcast through-cover silhouette to mark this
        // interactive as "seen-recently" so the player can spot it again
        // briefly through walls if they turn or step behind something.
        return rayResult.Count == 0 ? 1f : 0f;
    }
}
