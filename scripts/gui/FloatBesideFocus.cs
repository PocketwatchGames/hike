using Godot;

// Floats `_target` beside the container of whatever control holds keyboard /
// gamepad focus — an item slot also grabs focus on mouse hover, so the cursor
// drives it too. Anchoring to the container (the grid, the equip row, the recipe
// list) rather than the focused slot keeps the target still while the player
// moves within one, so it only moves when focus crosses into another. It goes to
// the preferred side when it fits there, else the other, top-aligned with the
// container and clamped fully on screen. Outward is the exception: it stays on
// its side pinned to the screen edge, overlapping its own container a little,
// rather than flip over whatever sits toward the middle. For an info panel that describes the
// highlighted thing.
//
// The target is taken out of container layout (TopLevel), but TopLevel does not
// change draw order: keep it last among the screen's children so it covers
// them. It should also disable mouse and focus for its whole subtree
// (mouse_behavior_recursive / focus_behavior_recursive), or it blocks hovering
// whatever it lies over.
[GlobalClass]
public partial class FloatBesideFocus : Node
{
	public enum ESide
	{
		Right,
		Left,
		// Away from the screen's center: left of a container on the left half,
		// right of one on the right half, so it covers the screen's edge rather
		// than whatever sits between the panels. Pinned to the edge when the
		// margin is a little short (see _maxEdgeOverlap).
		Outward,
	}

	[Export] private Control _target;
	[Export] private ESide _preferredSide = ESide.Right;
	// Pixels between the focused control's container and the target.
	[Export(PropertyHint.Range, "0,100,1")] private float _gap = 12f;
	// Pixels the target keeps from the screen edges.
	[Export(PropertyHint.Range, "0,100,1")] private float _screenMargin = 10f;
	// Outward only: how far, in pixels, the target may be pushed back over its own
	// container by the screen edge before it gives up and flips inward instead.
	[Export(PropertyHint.Range, "0,500,1")] private float _maxEdgeOverlap = 160f;

	// The focus owner last seen and the container it resolved to.
	Control _focus;
	// What the last placement was for; re-placed only when one of them moves.
	Control _anchor;
	Rect2 _anchorRect;
	Vector2 _size;

	public override void _Ready()
	{
		if (_target != null)
		{
			_target.TopLevel = true;
		}
	}

	public override void _Process(double delta)
	{
		if (_target == null || !_target.IsVisibleInTree())
		{
			_focus = null;
			_anchor = null;
			return;
		}
		Control focus = _target.GetViewport().GuiGetFocusOwner();
		if (focus == null)
		{
			return;
		}
		Control anchor = focus == _focus && IsInstanceValid(_anchor) ? _anchor : ContainerOf(focus);
		_focus = focus;
		// Content changes height (ingredients, salvage rows), so size to it first.
		_target.ResetSize();
		Vector2 size = _target.Size;
		Rect2 rect = anchor.GetGlobalRect();
		if (anchor == _anchor && rect == _anchorRect && size == _size)
		{
			return;
		}
		_anchor = anchor;
		_anchorRect = rect;
		_size = size;
		Place(rect, size);
	}

	// The container holding the focused thing: an item slot's parent when focus
	// is inside one (the slot's own button holds it), else the focused control's
	// parent. Walked only when focus changes hands.
	static Control ContainerOf(Control focus)
	{
		Node node = focus;
		while (node != null && node is not ItemSlotPanel)
		{
			node = node.GetParent();
		}
		Control item = node as Control ?? focus;
		return item.GetParent() as Control ?? item;
	}

	void Place(Rect2 anchor, Vector2 size)
	{
		Rect2 screen = _target.GetViewportRect().Grow(-_screenMargin);
		float right = anchor.End.X + _gap;
		float left = anchor.Position.X - _gap - size.X;
		bool fitsRight = right + size.X <= screen.End.X;
		bool fitsLeft = left >= screen.Position.X;
		bool preferRight = _preferredSide switch
		{
			ESide.Left => false,
			ESide.Outward => anchor.GetCenter().X >= screen.GetCenter().X,
			_ => true,
		};
		bool goRight;
		if (_preferredSide == ESide.Outward)
		{
			// Short by this much on the outward side, so pinning there covers that
			// much of the container.
			float overlap = preferRight ? right + size.X - screen.End.X : screen.Position.X - left;
			bool fitsInward = preferRight ? fitsLeft : fitsRight;
			goRight = overlap <= _maxEdgeOverlap || !fitsInward ? preferRight : !preferRight;
		}
		else
		{
			goRight = preferRight ? fitsRight || !fitsLeft : fitsRight && !fitsLeft;
		}
		float x = Mathf.Clamp(goRight ? right : left, screen.Position.X, Mathf.Max(screen.Position.X, screen.End.X - size.X));
		float y = Mathf.Clamp(anchor.Position.Y, screen.Position.Y, Mathf.Max(screen.Position.Y, screen.End.Y - size.Y));
		_target.GlobalPosition = new Vector2(x, y);
	}
}
