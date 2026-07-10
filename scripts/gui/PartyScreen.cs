using Godot;
using System;
using System.Collections.Generic;

// Camp "Select Character" tab. The party members are the live characters
// standing around the campfire (GameClient.PartyPlayers). Highlighting one —
// mouse hover / click, or left-stick / arrow keys — outlines it with the same
// OutlineMask silhouette InteractiveMeshHighlight uses and fills the stats panel
// with that member. Selecting marks it as the roster's active member; control
// transfers to it when camp closes (CampScreen.Close → GameClient
// .SyncControlToActive), so the selected character is the one we control on
// leaving.
public partial class PartyScreen : Control
{
	[Export] PlayerStatsPanel _playerStatsPanel;
	[Export] ItemInfoPanel _meleePanel;
	[Export] ItemInfoPanel _rangedPanel;
	[Export] ButtonHint _buttonHintSelect;
	// Screen-pixel radius within which the mouse cursor picks a character.
	[Export] float _mousePickRadius = 90f;
	// World-space height above a member's feet used as its on-screen pick point
	// (roughly torso height) so the cursor lands on the body, not the ground.
	[Export] float _memberPickHeight = 1.0f;

	// Squared screen-pixel distance the cursor must move before mouse-hover is
	// allowed to change the highlight. A stationary cursor must NOT re-pick every
	// frame — otherwise it overrides a gamepad/keyboard cycle and reads as
	// flicker / unresponsive selection.
	const float MouseMoveEpsilonSq = 1f;

	GameClient _gameClient;
	IReadOnlyList<Player> _members;
	int _highlightIndex = -1;
	Vector2 _lastMousePos;
	bool _mouseWasPressed;
	// Invoked when a member is selected. Set in the forced death party-select
	// (CampScreen closes on select); null in normal camp (the switch is deferred
	// to camp close).
	Action _onSelected;

	public override void _Ready()
	{
		// Hidden until its tab opens — otherwise the scene's default visibility
		// leaves it overlapping the default (Sleep) sub-screen on first camp open.
		Visible = false;
	}

	public void Open(GameClient gameClient, Action onSelected = null)
	{
		Visible = true;
		_gameClient = gameClient;
		_members = gameClient?.PartyPlayers;
		_onSelected = onSelected;
		_buttonHintSelect?.SetHint("ui_select", "Select");
		_lastMousePos = GetViewport().GetMousePosition();
		_mouseWasPressed = Input.IsMouseButtonPressed(MouseButton.Left);
		// Start focused on the currently-controlled member; if it somehow fell,
		// fall back to the first living member.
		int start = gameClient?.ActivePartyIndex ?? 0;
		if (!IsSelectable(start))
		{
			start = -1;
			for (int i = 0; i < (_members?.Count ?? 0); i++)
			{
				if (IsSelectable(i)) { start = i; break; }
			}
		}
		SetHighlight(start);
	}

	public void Close()
	{
		SetHighlight(-1);
		Visible = false;
		_gameClient = null;
		_members = null;
		_onSelected = null;
	}

	public override void _Process(double delta)
	{
		if (!Visible || _members == null || _members.Count == 0)
		{
			return;
		}
		Vector2 mouse = GetViewport().GetMousePosition();
		// Mouse only drives the highlight while it's actually moving, so it can't
		// stomp a stick/keyboard cycle on a still cursor.
		if (mouse.DistanceSquaredTo(_lastMousePos) > MouseMoveEpsilonSq)
		{
			int hover = PickMemberUnderMouse(mouse);
			if (hover >= 0)
			{
				SetHighlight(hover);
			}
		}
		_lastMousePos = mouse;

		// Left-click selects the character under the cursor. Edge-detected via
		// polling rather than _UnhandledInput so a mouse-filtering panel above the
		// characters can't swallow the click.
		bool pressed = Input.IsMouseButtonPressed(MouseButton.Left);
		if (pressed && !_mouseWasPressed)
		{
			int clicked = PickMemberUnderMouse(mouse);
			if (clicked >= 0)
			{
				SetHighlight(clicked);
				Select();
			}
		}
		_mouseWasPressed = pressed;

		// Keyboard / gamepad cycle + select. Polled with IsActionJustPressed so a
		// held stick fires exactly once per flick (an analog axis can emit a press
		// event every frame it's beyond the deadzone, which read as flicker).
		if (Input.IsActionJustPressed("ui_left"))
		{
			Cycle(-1);
		}
		else if (Input.IsActionJustPressed("ui_right"))
		{
			Cycle(1);
		}
		if (Input.IsActionJustPressed("ui_select"))
		{
			Select();
		}
	}

	// Index of the member whose on-screen position is nearest the cursor within
	// the pick radius, or -1 if none qualifies.
	int PickMemberUnderMouse(Vector2 mouse)
	{
		if (_gameClient == null)
		{
			return -1;
		}
		int best = -1;
		float bestDistSq = _mousePickRadius * _mousePickRadius;
		for (int i = 0; i < _members.Count; i++)
		{
			Player p = _members[i];
			if (p == null || !IsSelectable(i))
			{
				continue;
			}
			Vector2 screen = _gameClient.ProjectToScreen(p.GlobalPosition + Vector3.Up * _memberPickHeight);
			float d = mouse.DistanceSquaredTo(screen);
			if (d < bestDistSq)
			{
				bestDistSq = d;
				best = i;
			}
		}
		return best;
	}

	void SetHighlight(int index)
	{
		if (index == _highlightIndex)
		{
			return;
		}
		MemberAt(_highlightIndex)?.SetHighlighted(false);
		_highlightIndex = index;
		Player member = MemberAt(index);
		member?.SetHighlighted(true);
		_playerStatsPanel?.SetPlayer(member);
		Inventory inv = member?.Inventory;
		_meleePanel?.SetItem(inv?.GetWeapon(EInventorySlot.WeaponMelee), forceIdentified: true);
		_rangedPanel?.SetItem(inv?.GetWeapon(EInventorySlot.WeaponRanged), forceIdentified: true);
	}

	Player MemberAt(int index) =>
		(_members != null && index >= 0 && index < _members.Count) ? _members[index] : null;

	// A fallen member is a corpse out in the field, not a candidate here — only
	// living members can be highlighted / selected.
	bool IsSelectable(int index)
	{
		Player p = MemberAt(index);
		return p != null && p.Member is not { IsDead: true };
	}

	// Mark the highlighted member as the roster's active member (control
	// transfers on camp exit). Re-selecting the current active is a harmless
	// no-op.
	void Select()
	{
		if (_gameClient != null && IsSelectable(_highlightIndex))
		{
			_gameClient.SetPartyActive(_highlightIndex);
			// In the forced death select this confirms + closes the screen; in
			// normal camp it's null and the switch is applied on camp close.
			_onSelected?.Invoke();
		}
	}

	// Move the highlight to the next living member in `direction`, skipping any
	// fallen members.
	void Cycle(int direction)
	{
		if (_members == null || _members.Count == 0)
		{
			return;
		}
		int count = _members.Count;
		int idx = _highlightIndex < 0 ? 0 : _highlightIndex;
		for (int step = 0; step < count; step++)
		{
			idx = ((idx + direction) % count + count) % count;
			if (IsSelectable(idx))
			{
				SetHighlight(idx);
				return;
			}
		}
	}
}
