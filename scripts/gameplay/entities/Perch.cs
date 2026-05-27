using Godot;

// A landing spot a flying mob can rest on, authored as a marker placed inside
// a prop's billboard sprite. Authoring is in the sprite's 2D space: in the
// editor the sprite renders as a flat, face-on quad (billboarding is shader-
// side and OFF in-editor), and this marker pins its own local Z to 0 so it can
// only be slid in X/Y across the art — drag it (via the standard move gizmo)
// onto the painted branch/post. Make it a child of the prop's LitSprite.
//
// Because the sprite billboards and can mirror at runtime, the painted landing
// point isn't a fixed world coordinate — it orbits the sprite origin as the
// camera yaws and flips horizontally when the sprite mirrors. So the perch
// keeps a stable gameplay anchor (WorldPosition, used for flee queries and
// claims) plus ResolveLandingPosition(camera), which re-projects the authored
// sprite-space offset into world space each frame for the visual landing.
[Tool]
[GlobalClass]
public partial class Perch : Node3D
{
    // Facing (yaw, radians) the landed bird adopts. Cosmetic.
    [Export] public float FacingYaw;

    // The mob occupying or inbound to this perch, or null. Transient runtime
    // state, never serialized.
    public Node3D Occupant;

    public bool IsFree => Occupant == null || !IsInstanceValid(Occupant);

    // The prop sprite this perch is attached to (nearest LitSprite ancestor),
    // and the authored offset of the landing point in that sprite's local 2D
    // frame (x = horizontal across the art, y = vertical). Captured at runtime.
    private LitSprite _sprite;
    private float _offX;
    private float _offY;

    // Stable gameplay anchor: the sprite's world column at the branch height
    // (camera-independent, so flee targeting and distance don't wobble as the
    // view rotates). Falls back to the node position if no sprite was found.
    public Vector3 WorldPosition
    {
        get
        {
            if (_sprite != null)
            {
                Vector3 o = _sprite.GlobalPosition;
                return new Vector3(o.X, o.Y + _offY, o.Z);
            }
            return GlobalPosition;
        }
    }

    public bool TryClaim(Node3D mob)
    {
        if (!IsFree && Occupant != mob)
        {
            return false;
        }
        Occupant = mob;
        return true;
    }

    public void Release(Node3D mob)
    {
        if (Occupant == mob)
        {
            Occupant = null;
        }
    }

    public override void _Ready()
    {
        if (Engine.IsEditorHint())
        {
            return;
        }
        _sprite = FindSprite();
        if (_sprite != null)
        {
            // Offset of the landing point in the sprite's local frame, robust to
            // nesting and the sprite's own transform.
            Vector3 local = _sprite.ToLocal(GlobalPosition);
            _offX = local.X;
            _offY = local.Y;
        }
        World.Current?.Perches.Add(this);
    }

    public override void _Process(double delta)
    {
        // Editor only: keep the marker pinned to the sprite's 2D plane so it can
        // only be lined up in X/Y against the art (assumes it's a child of the
        // LitSprite, the intended setup).
        if (Engine.IsEditorHint() && Position.Z != 0f)
        {
            Position = new Vector3(Position.X, Position.Y, 0f);
        }
    }

    public override void _ExitTree()
    {
        if (Engine.IsEditorHint())
        {
            return;
        }
        World.Current?.Perches.Remove(this);
    }

    // World position of the painted landing point for `cam`. Upright billboard:
    // the art's horizontal maps to the camera's flattened right (negated when
    // the sprite is mirrored, so the bird follows a flipped branch), the art's
    // vertical maps to world up, and ForwardOffset nudges toward the camera so
    // the bird shares the sprite's depth plane (then sorts in front via its own
    // ForwardOffset). Falls back to the gameplay anchor if there's no sprite/cam.
    public Vector3 ResolveLandingPosition(Camera3D cam)
    {
        if (_sprite == null || cam == null)
        {
            return WorldPosition;
        }
        Vector3 right = cam.GlobalBasis.X;
        right.Y = 0f;
        right = right.LengthSquared() > 1e-6f ? right.Normalized() : Vector3.Right;
        Vector3 toward = cam.GlobalBasis.Z;
        toward.Y = 0f;
        toward = toward.LengthSquared() > 1e-6f ? toward.Normalized() : Vector3.Back;

        float hx = _sprite.EffectiveMirror ? -_offX : _offX;
        return _sprite.GlobalPosition + right * hx + Vector3.Up * _offY + toward * _sprite.ForwardOffset;
    }

    private LitSprite FindSprite()
    {
        Node n = GetParent();
        while (n != null)
        {
            if (n is LitSprite sprite)
            {
                return sprite;
            }
            n = n.GetParent();
        }
        return null;
    }
}
