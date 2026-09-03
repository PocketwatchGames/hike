using Godot;
using Godot.Collections;

// An OBJECT a climber can hold on to, as opposed to rock dressed in something
// grippable. A dropped rope today; ladders, vines and rigging are the same
// shape.
//
// The climb was voxel-backed end to end: every hold had to resolve to a solid
// voxel whose face was marked climbable (ClimbProbe.IsClimbableFace). A rope
// hangs in air — over an overhang there is no voxel behind it at all — so this
// is the second answer to "is what I am touching climbable", checked before the
// voxel march and short-circuiting it.
//
// It is also the INTERACTIVE you take hold of. Getting on is an interact, not
// the Dash traversal press, because the traversal press finds its wall by
// casting a short ray along body facing — which is the right question for a
// cliff you are walking into and the wrong one for a rope: at the top the ledge
// barrier holds the player back from the lip, so the rope is out of that reach
// and slightly off their facing, and the press finds nothing. An interact box
// spanning the whole line answers "am I at the rope" instead of "am I looking
// exactly at it". The Dash entry still works where it works (walking into the
// line from below); this is the one that works everywhere.
//
// Its climbing collider sits alone on ECollisionLayer.Climbable, so only the
// climb queries see it: the player walks straight through a rope, arrows and
// sight lines ignore it, and it is not cover.
[GlobalClass]
public partial class ClimbableSurface : StaticBody3D, IInteractive
{
    // Which way a climber hangs off this. Zero (the default) means "whichever
    // way you touched it" — the right answer for a flat authored panel.
    //
    // A rope sets it, and must: the ray normal off a round rope is radial, so a
    // player who grabbed it from the side would hang beside the cliff instead of
    // facing it — the climb animation would point the wrong way and, worse,
    // TryClimbTopOut probes along -normal and would look out into open air
    // rather than at the ledge, leaving a climber who reached the top with
    // nothing to top out onto. Held in WORLD space: a rope's outward direction
    // is decided when it is dropped and never turns after that.
    public Vector3 GripNormalOverride { get; set; }

    // Where a climber who reaches the top of this steps off, or null to let the
    // player work it out from their own position. A rope sets it, and must: the
    // top-out probe reaches climbReach horizontally along the hold's inward
    // normal, and a rope hangs its climber far enough off the rock that whether
    // that probe lands on the ledge column or in the open air beside it comes
    // down to centimetres of clearance. The rope knows exactly which cell it is
    // tied to, so it says so instead. Still checked against the walk field —
    // this names a column, it does not grant a landing.
    public Vector3? TopOutTarget { get; set; }

    // How far the hold's surface stands off its centre line. The interact entry
    // places the body without a ray, so it needs the number the ray would have
    // measured; a climber ends up this plus climbWallOffset out from the line.
    public float GripRadius { get; set; }

    // Top and bottom of the line, in world Y. The node itself sits at the middle
    // (so the interact highlight, which ranks by node distance, is fair to a
    // climber anywhere along a tall rope), which is why these are stored rather
    // than derived from the transform.
    public float LineTopY { get; set; }
    public float LineBottomY { get; set; }

    // Whether a climber may traverse sideways along this. False for a rope:
    // there is nothing sideways to reach, and the lateral input would push the
    // body off the line the hold sits on so the contact fan spends every tick
    // finding it again — the climb reads as rubber.
    [Export] public bool allowsLateral = true;

    // Offered on interact. Authored upstream (the coil's scene) and handed down,
    // so the label and icon stay authoring rather than code.
    public Array<InteractiveAction> Actions { get; set; }

    // Stashed from the highlight poll so Complete, which only receives an action
    // index, knows who is taking hold.
    private Player _actor;

    // At the climber's own height on the line, not at its top: on a long rope a
    // prompt pinned to the anchor is metres above someone standing at the foot
    // of it.
    public Vector3 hudPosition
    {
        get
        {
            Vector3 line = GlobalPosition;
            float y = _actor != null
                ? Mathf.Clamp(_actor.GlobalPosition.Y + 1f, LineBottomY, LineTopY)
                : LineTopY;
            return new Vector3(line.X, y, line.Z);
        }
    }

    public bool CanInteract()
    {
        return Actions != null && Actions.Count > 0;
    }

    public bool CanActorInteract(Player player)
    {
        if (player == null || !CanInteract())
        {
            return false;
        }
        _actor = player;
        // Already on a rope (or a wall): the prompt would sit on screen for the
        // whole climb offering to start the thing being done.
        return !player.Climbing;
    }

    // Nothing to show through a wall — the silhouette pass wants a mesh.
    public bool ShouldShowXray() => false;

    public Array<InteractiveAction> GetActions(Player player)
    {
        return CanActorInteract(player) ? Actions : null;
    }

    // Deferred by a tick like the mantle's: the runner is still tearing this
    // action down, so a climb started here would fail its own runner-busy gate.
    public void Complete(int actionIndex)
    {
        _actor?.OnClimbSurfaceInteractComplete(this);
    }

    // The hold normal to adopt for a contact, given the raw surface normal the
    // ray reported.
    public Vector3 GripNormal(Vector3 contactNormal)
    {
        return GripNormalOverride.LengthSquared() > 1e-6f ? GripNormalOverride : contactNormal;
    }

    // The surface owning a collider a climb query hit, or null. The body IS the
    // surface today; resolving through the parent chain keeps an authored scene
    // free to hang the shape a level down.
    public static ClimbableSurface From(GodotObject collider)
    {
        Node node = collider as Node;
        while (node != null)
        {
            if (node is ClimbableSurface surface)
            {
                return surface;
            }
            node = node.GetParent();
        }
        return null;
    }
}
