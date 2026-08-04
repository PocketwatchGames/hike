using System.Collections.Generic;
using Godot;

// Snaps a target 3D visual's world position onto the camera's pixel grid — the
// same grid ViewportRig.SnapAndUpscale snaps the camera to. A moving model
// otherwise drifts sub-pixel against the snapped camera and terrain, so its
// silhouette shimmers/crawls; pinning the visual to the same grid keeps it crisp
// and makes its motion read as whole-pixel steps.
//
// Sibling driver wired via [Export] _target (mirrors ModelAnimator /
// HeldItemVisual), so any 3D entity — character, prop, vehicle — opts in by
// adding this node and pointing _target at its model. No reparenting needed,
// and only the translation channel is touched, so it coexists with whatever
// writes the target's rotation (e.g. ModelAnimator's 8-way facing snap).
// Sprite-based visuals are simply never given one.
//
// TWO THINGS KEEP THIS CHEAP, and both matter at 139 resident mobs:
//
// 1. DRIVEN CENTRALLY, not by per-node _Process. GameClient calls TickAll once
//    per frame right after the camera snap. The camera-derived half of the maths
//    (texel size, right/up axes, the ortho check) is identical for every
//    instance and every term of it is a managed→native boundary crossing.
//
// 2. IDLE INSTANCES DO NO WORK. The snapped result only changes when the parent
//    moves, the camera rotates, or zoom changes — so each instance compares its
//    unsnapped origin against last frame's and bails before writing. That write
//    is the expensive half: assigning GlobalPosition dirties the target and
//    propagates a transform notification through the entire model subtree
//    (skeleton, every mesh, every BoneAttachment3D — ~28 nodes for a goblin),
//    and almost every mob is standing still on any given frame.
//
// Together these took it from the most expensive _Process section in the game
// (0.715 ms/frame across 141 nodes) to a shared resolve plus one transform read
// per node.
[GlobalClass]
public partial class PixelSnap : Node
{
	// The visual whose world position is quantized. Only GlobalPosition is
	// written; rotation/scale are left untouched.
	[Export] private Node3D _target;

	// Every live instance, ticked as one managed loop by TickAll.
	private static readonly List<PixelSnap> _live = new();

	private Node3D _parent;
	// The target's authored local offset, captured once. The snap is derived
	// from this each frame rather than from the target's live transform, so the
	// applied correction never feeds back into itself (see Apply).
	private Vector3 _baseLocalPos;
	private bool _haveBase;
	// Last unsnapped origin this instance resolved, for the idle check above.
	private Vector3 _lastIntended;
	private bool _haveLastIntended;

	public override void _Ready()
	{
		if (_target != null)
		{
			_parent = _target.GetParent() as Node3D;
			_baseLocalPos = _target.Position;
			_haveBase = _parent != null;
		}
		// TickAll owns the per-frame work; a per-node callback would reintroduce
		// the dispatch this class exists to avoid.
		SetProcess(false);
		_live.Add(this);
	}

	public override void _ExitTree()
	{
		_live.Remove(this);
	}

	// Called by GameClient once per frame, AFTER ViewportRig.SnapAndUpscale, so
	// every visual lands on the same absolute world grid the camera just snapped
	// to. Bailing (no camera, perspective projection) leaves visuals unsnapped,
	// the same fallback the per-node version had.
	public static void TickAll(Camera3D camera)
	{
		using var _prof = Profiler.Sample("PixelSnap.TickAll");
		if (_live.Count == 0 || camera == null || !IsInstanceValid(camera))
		{
			return;
		}
		// The pixel grid only exists under an orthographic camera, where
		// camera.Size is a world-space extent; under perspective there is no
		// fixed world-units-per-texel and snapping would be meaningless.
		if (camera.Projection != Camera3D.ProjectionType.Orthogonal)
		{
			return;
		}
		Viewport viewport = camera.GetViewport();
		if (viewport == null)
		{
			return;
		}
		// World units per inner-viewport texel — identical to ViewportRig's
		// `chunky`. camera.Size spans the viewport's Y texels; Godot derives
		// horizontal size from aspect, so texel width in world equals this too.
		float viewH = Mathf.Max(1f, viewport.GetVisibleRect().Size.Y);
		float chunky = camera.Size / viewH;
		if (chunky <= 0f)
		{
			return;
		}
		Basis basis = camera.GlobalBasis;
		Vector3 right = basis.X;
		Vector3 up = basis.Y;
		// The grid is absolute world space, so camera TRANSLATION doesn't move
		// it — only a rotation or a zoom does. When neither changed, an instance
		// whose parent also hasn't moved can skip its write entirely.
		bool gridChanged = chunky != _lastChunky || right != _lastRight || up != _lastUp;
		_lastChunky = chunky;
		_lastRight = right;
		_lastUp = up;

		for (int i = 0; i < _live.Count; i++)
		{
			_live[i].Apply(chunky, right, up, gridChanged);
		}
	}

	private static float _lastChunky;
	private static Vector3 _lastRight;
	private static Vector3 _lastUp;

	private void Apply(float chunky, Vector3 right, Vector3 up, bool gridChanged)
	{
		if (!_haveBase || _target == null)
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
		if (!gridChanged && _haveLastIntended && intended == _lastIntended)
		{
			// Nothing this instance's snap depends on has moved — the target is
			// already sitting on the right grid point.
			return;
		}
		_lastIntended = intended;
		_haveLastIntended = true;

		// Project onto the camera's right/up axes and Floor each to a chunky
		// multiple — exactly the camera's own snap rule, so the visual lands on
		// the same absolute world grid as the snapped terrain and rides the
		// upscale shader's uniform sub-texel offset with it (no relative crawl).
		// Depth (forward) is left continuous; under ortho it never moves a pixel.
		float rx = right.Dot(intended);
		float ry = up.Dot(intended);
		float sx = Mathf.Floor(rx / chunky) * chunky;
		float sy = Mathf.Floor(ry / chunky) * chunky;
		_target.GlobalPosition = intended + (sx - rx) * right + (sy - ry) * up;
	}
}
