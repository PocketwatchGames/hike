using Godot;

// Snaps a target 3D visual's world position onto the camera's pixel grid every
// frame — the same grid GameClient.SnapCameraAndUpdateUpscale snaps the camera
// to. A moving model otherwise drifts sub-pixel against the snapped camera and
// terrain, so its silhouette shimmers/crawls; pinning the visual to the same
// grid keeps it crisp and makes its motion read as whole-pixel steps.
//
// Sibling driver wired via [Export] _target (mirrors ModelAnimator /
// HeldItemVisual), so any 3D entity — character, prop, vehicle — opts in by
// adding this node and pointing _target at its model. No reparenting needed,
// and only the translation channel is touched, so it coexists with whatever
// writes the target's rotation (e.g. ModelAnimator's 8-way facing snap).
// Sprite-based visuals are simply never given one.
[GlobalClass]
public partial class PixelSnap : Node
{
	// The visual whose world position is quantized. Only GlobalPosition is
	// written; rotation/scale are left untouched.
	[Export] private Node3D _target;

	private Camera3D _camera;
	private Node3D _parent;
	// The target's authored local offset, captured once. The snap is derived
	// from this each frame rather than from the target's live transform, so the
	// applied correction never feeds back into itself (see _Process).
	private Vector3 _baseLocalPos;
	private bool _haveBase;

	public override void _Ready()
	{
		if (_target != null)
		{
			_parent = _target.GetParent() as Node3D;
			_baseLocalPos = _target.Position;
			_haveBase = _parent != null;
		}
	}

	public override void _Process(double delta)
	{
		if (_target == null || !_haveBase)
		{
			return;
		}
		if (_camera == null || !IsInstanceValid(_camera))
		{
			_camera = _target.GetViewport()?.GetCamera3D();
			if (_camera == null)
			{
				return;
			}
		}
		// The pixel grid only exists under an orthographic camera, where
		// camera.Size is a world-space extent; under perspective there is no
		// fixed world-units-per-texel and snapping would be meaningless.
		if (_camera.Projection != Camera3D.ProjectionType.Orthogonal)
		{
			return;
		}

		// World units per inner-viewport texel — identical to GameClient's
		// `chunky`. camera.Size spans the viewport's Y texels; Godot derives
		// horizontal size from aspect, so texel width in world equals this too.
		float viewH = Mathf.Max(1f, _camera.GetViewport().GetVisibleRect().Size.Y);
		float chunky = _camera.Size / viewH;
		if (chunky <= 0f)
		{
			return;
		}

		// The model's UNSNAPPED world origin, recomputed fresh from the parent
		// every frame. Reading _target.GlobalPosition instead would feed last
		// frame's snap correction back in as input — and because the parent
		// yaws as the actor turns, that baked-in offset rotates and compounds,
		// so the visual drifts (sinks through the floor) the longer you move.
		// Deriving from the parent keeps the input clean, so the snapped result
		// is always within one texel of the true position.
		Vector3 intended = _parent.GlobalTransform * _baseLocalPos;

		// Project onto the camera's right/up axes and Floor each to a chunky
		// multiple — exactly the camera's own snap rule, so the visual lands on
		// the same absolute world grid as the snapped terrain and rides the
		// upscale shader's uniform sub-texel offset with it (no relative crawl).
		// Depth (forward) is left continuous; under ortho it never moves a pixel.
		Basis basis = _camera.GlobalBasis;
		Vector3 right = basis.X;
		Vector3 up = basis.Y;
		float rx = right.Dot(intended);
		float ry = up.Dot(intended);
		float sx = Mathf.Floor(rx / chunky) * chunky;
		float sy = Mathf.Floor(ry / chunky) * chunky;
		_target.GlobalPosition = intended + (sx - rx) * right + (sy - ry) * up;
	}
}
