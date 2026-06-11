using Godot;

// The visible in-hand torch prop, shared by the player (via HeldItemVisual) and
// mobs. It is purely cosmetic: a wood handle plus a head that swaps between an
// unlit (charred) look and a lit (glowing) look, with the carried-light flame fx
// parented to the head while lit. The MovingLight that actually deposits light
// into the world is a SEPARATE node on the carrier root — this scene only draws
// the prop and its flame particles, which used to ride the player/mob root and
// have moved here so the flame reads as coming off the torch tip.
//
// SetLit toggles the two head visuals and spawns/stops the loop fx. The flame
// scene's particles are authored at ~0.85m up, which lands on the head when the
// fx parents at this scene's origin, so flameAnchor defaults to this node.
[GlobalClass]
public partial class HeldTorch : Node3D
{
    // Head meshes toggled by lit state — the charred head when unlit, the
    // glowing (emissive) head when lit.
    [Export] public Node3D unlitHead;
    [Export] public Node3D litHead;

    // Where the flame loop fx parents when lit. Left null = this node's origin,
    // which aligns the fx's authored ~0.85m flame height with the torch head.
    [Export] public Node3D flameAnchor;

    // The looping flame + ember + crackle effect (the same torch_loop scene the
    // player-carried MovingLight used to spawn at the player root).
    [Export] public PackedScene flameLoopScene;

    private Fx _flame;
    private bool _lit;

    public override void _Ready()
    {
        // Seed the visual state; no fx spawns here (default is unlit) so this is
        // safe to run synchronously during the instancing AddChild storm.
        ApplyLit();
    }

    public void SetLit(bool lit)
    {
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
        }
        else if (_flame != null)
        {
            // Loop fx fades itself out after Stop (grace window for in-flight
            // particles + audio), so the flame doesn't pop off abruptly.
            _flame.Stop();
            _flame = null;
        }
    }
}
