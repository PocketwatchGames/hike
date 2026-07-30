using Godot;

// The visible torch prop, shared by the player (via HeldItemVisual) and mobs. A
// wood handle plus a head that swaps between an unlit (charred) and a lit
// (glowing) look, with the carried-light flame fx parented to the head while lit.
// This scene also OWNS the torch's world light: while lit it spawns its
// movingLightScene as a child of the carrier ROOT (passed to SetLit), not of this
// prop — so the light samples from the stable body origin instead of swinging
// with the hand bone, while its lifetime still rides with the torch.
//
// SetLit toggles the head visuals, spawns/stops the loop fx, and brings the world
// light up/down. torch_loop's particles emit at the fx's own origin, so the flame
// lands wherever flameAnchor sits — wire it to the head, not the handle base.
[GlobalClass]
public partial class HeldTorch : Node3D
{
    // Head meshes toggled by lit state — the charred head when unlit, the
    // glowing (emissive) head when lit.
    [Export] public Node3D unlitHead;
    [Export] public Node3D litHead;

    // Where the flame loop fx parents when lit. Left null = this node's origin,
    // i.e. the bottom of the handle — wire it to the head.
    [Export] public Node3D flameAnchor;

    // The looping flame + ember + crackle effect (torch_loop scene).
    [Export] public PackedScene flameLoopScene;

    // The world-light this torch deposits while lit. Spawned onto the carrier root
    // (the lightParent passed to SetLit) so the deposit tracks the body rather
    // than the hand bone. Null = a purely cosmetic torch that emits no light.
    [Export] public PackedScene movingLightScene;

    // Marks a torch that should be lit from spawn — a torch used as a weapon's
    // heldModel (a goblin's burning torch), which the weapon channel instances but
    // never drives via SetLit. HeldItemVisual reads this and lights such a torch
    // with the carrier root (so it casts world light too); this class doesn't
    // self-light, since it has no carrier root of its own to deposit light from.
    [Export] public bool startLit = false;

    private Fx _flame;
    private MovingLight _movingLight;
    private Node3D _lightParent;
    private bool _lit;

    public override void _Ready()
    {
        // Seed the visual state; no fx/light spawns here (default is unlit) so
        // this is safe to run synchronously during the instancing AddChild storm.
        // A startLit weapon torch is lit by HeldItemVisual once its carrier root
        // is known (it can't deposit world light without one).
        ApplyLit();
    }

    // lightParent is the node the world light attaches to while lit — the carrier
    // root (Mob / Player). Latched so a later relight reuses it.
    public void SetLit(bool lit, Node3D lightParent = null)
    {
        if (lightParent != null)
        {
            _lightParent = lightParent;
        }
        if (lit == _lit)
        {
            return;
        }
        _lit = lit;
        ApplyLit();
    }

    private void ApplyLit()
    {
        if (unlitHead != null)
        {
            unlitHead.Visible = !_lit;
        }
        if (litHead != null)
        {
            litHead.Visible = _lit;
        }

        if (_lit)
        {
            if (_flame == null && flameLoopScene != null)
            {
                _flame = Fx.Create(flameLoopScene, flameAnchor ?? this, Vector3.Zero);
            }
            if (_movingLight == null && movingLightScene != null && _lightParent != null)
            {
                _movingLight = movingLightScene.Instantiate<MovingLight>();
                _lightParent.AddChild(_movingLight);
            }
        }
        else
        {
            if (_flame != null)
            {
                // Loop fx fades itself out after Stop (grace window for in-flight
                // particles + audio), so the flame doesn't pop off abruptly.
                _flame.Stop();
                _flame = null;
            }
            if (_movingLight != null)
            {
                // Hand off so the light fades out (its authored off-cue) and frees
                // itself rather than cutting abruptly.
                _movingLight.Deactivate(freeWhenDone: true);
                _movingLight = null;
            }
        }
    }

    public override void _ExitTree()
    {
        // Godot fires _ExitTree on a benign REPARENT (RemoveChild→AddChild), not
        // only on destruction — and HeldItemVisual.UpdateTorchPlacement reparents
        // this prop hand↔belt the moment it lights. Only drop the world light when
        // this node is actually being freed; otherwise lighting the torch would
        // tear down the very light it just spawned. The light lives on the carrier
        // root (not this prop), so it rides through the reparent untouched.
        if (!IsQueuedForDeletion())
        {
            return;
        }
        // The prop is being torn down (torch swapped out / carrier despawned).
        // Drop the world light silently — never fire the off-cue from _ExitTree
        // (its Fx.Create would target a dying parent). The player-initiated douse
        // path goes through SetLit(false), which fires the cue.
        if (_movingLight != null)
        {
            _movingLight.QueueFree();
            _movingLight = null;
        }
    }
}
