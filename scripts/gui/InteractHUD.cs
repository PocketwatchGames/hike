using Godot;
using Godot.Collections;

// Per-interactive HUD that:
//   * Renders the default-action icon while the player is highlighted on
//     the interactive.
//   * Surfaces a hold-bar (_holdContainer / _holdTimer) when more than one
//     action is available, driven by Player.InteractHoldProgress.
//   * Pops a modal options panel (_interactOptionsParent) when the player
//     finishes the hold; sets GameClient.InputSuppressed so gameplay input
//     is dropped until the player selects an action or hits ui_cancel.
//   * While the runner is executing the picked action, _icon switches to
//     that action's icon and _interactTimer fills with ClientInteractProgress.
//
// Lifecycle: spawned by GameClient when player highlights or starts an
// interaction; freed when the player has neither a highlight nor a
// current interactive.
public partial class InteractHUD : Node2D
{
	[Export] private TextureProgressBar _interactTimer;
	[Export] private ProgressBar _holdTimer;
	[Export] private TextureRect _icon;
	[Export] private Control _holdContainer;
	[Export] private Control _interactOptionsParent;
	[Export] private Control _interactOptionsContainer;
	[Export] private PackedScene _interactOptionScene;

	// Level star pips (fan of up to five), lit to _interactive.InteractLevel.
	// Mirrors the mob HUD's difficulty fan. Hidden entirely for level-0
	// interactives (everything but the forge today).
	[Export] private Control _levelContainer;
	[Export] private Array<TextureRect> _levelPips = new();
	// Radius of the pip arc (px) and angular spacing between adjacent pips.
	[Export] private float _pipArcRadius = 20f;
	[Export(PropertyHint.Range, "0,90")] private float _pipArcSpacingDegrees = 32f;

	// Icon/button tint applied to an action whose pooled ingredient cost
	// (InteractiveAction.reagents) the player can't currently afford — a muted red
	// that reads as "blocked" before the press is refused. Composes with the HUD's
	// preview/committed Modulate. Actions with no reagent cost are never tinted.
	[Export] private Color _unaffordableTint = new Color(1f, 0.45f, 0.45f, 0.6f);

	Camera3D _camera;
	Player _player;
	IInteractive _interactive;
	Array<InteractiveAction> _actions;
	// The merged option list (world actions + player self-actions) captured when the
	// options modal opens; drives the option buttons and the focused-option icon.
	// Null while the modal is closed.
	Array<InteractiveAction> _menuActions;
	bool _modalOpen;
	int _modalFocusedIndex = -1;
	int _interactLevel;

	// This HUD fronts the player's self-action menu (opened with nothing highlighted),
	// not a world interactive — it has no persistent prompt and auto-opens its modal.
	bool IsSelfMenu => _player != null && ReferenceEquals(_interactive, _player.SelfInteractive);

	// Total entries the options modal would show: this interactive's own actions plus
	// the always-available self-actions (unless this IS the self menu, which already
	// lists them). Drives the hold-to-open affordance so a single-action world
	// interactive still offers the hold path to reach the self-actions.
	int MenuCount() => (_actions?.Count ?? 0) + (IsSelfMenu ? 0 : _player?.SelfActions?.Count ?? 0);

	public IInteractive Interactive => _interactive;
	public bool ModalOpen => _modalOpen;

	public static InteractHUD Create(PackedScene scene, Camera3D camera, Player player, IInteractive interactive, Node parent)
	{
		var hud = scene.Instantiate<InteractHUD>();
		hud.Init(camera, player, interactive, parent);
		return hud;
	}

	void Init(Camera3D camera, Player player, IInteractive interactive, Node parent)
	{
		_camera = camera;
		_player = player;
		_interactive = interactive;
		_player.TreeExiting += QueueFree;
		_player.onInteractMenuOpenRequested += OnInteractMenuOpenRequested;
		if (parent != null)
		{
			parent.AddChild(this);
		}
		if (_interactOptionsParent != null)
		{
			_interactOptionsParent.Visible = false;
		}
		RefreshActions();
		SetupLevelPips();
		Update();
		// The self-action menu has no persistent prompt — it exists only to show the
		// options list, so pop the modal immediately (deferred so we're in the tree).
		// Skip when a self-action is already in flight: this HUD instance was respawned
		// just to show the running action's progress ring, not to reopen the menu.
		if (IsSelfMenu && _player.CurInteractive == null)
		{
			CallDeferred(MethodName.OpenModal);
		}
	}

	// Light one pip per level (fixed at spawn — an interactive's level is
	// immutable) and lay them out in a downward fan, mirroring MobHUD. The
	// container's on-screen visibility is resolved per-frame in Update.
	void SetupLevelPips()
	{
		_interactLevel = _interactive.InteractLevel;
		for (int i = 0; i < _levelPips.Count; i++)
		{
			if (_levelPips[i] != null)
			{
				_levelPips[i].Visible = i < _interactLevel;
			}
		}
		LayoutPipArc(_interactLevel);
		if (_levelContainer != null)
		{
			_levelContainer.Visible = false;
		}
	}

	// Fan the lit pips along a downward arc centered under the HUD icon, so the
	// row stays symmetric regardless of count (a single pip lands dead-center).
	void LayoutPipArc(int count)
	{
		if (_levelPips == null || count <= 0)
		{
			return;
		}
		float step = Mathf.DegToRad(_pipArcSpacingDegrees);
		// Screen +Y is down, so π/2 points the fan straight down under the icon.
		const float centerAngle = Mathf.Pi * 0.5f;
		float startAngle = centerAngle - step * (count - 1) * 0.5f;
		for (int i = 0; i < count && i < _levelPips.Count; i++)
		{
			TextureRect pip = _levelPips[i];
			if (pip == null)
			{
				continue;
			}
			float angle = startAngle + step * i;
			Vector2 arcCenter = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * _pipArcRadius;
			pip.Position = arcCenter - pip.CustomMinimumSize * 0.5f;
		}
	}

	public override void _ExitTree()
	{
		if (_player != null)
		{
			_player.onInteractMenuOpenRequested -= OnInteractMenuOpenRequested;
		}
		if (_modalOpen)
		{
			// Closing while modal open — clear suppression so input doesn't
			// remain stuck off if the HUD is freed mid-modal (e.g. interactive
			// despawns while options are visible).
			GameClient gc = GameClient.Current;
			if (gc != null)
			{
				gc.InputSuppressed = false;
			}
			_player?.CloseInteractMenu();
		}
	}

	void RefreshActions()
	{
		_actions = _interactive.GetActions(_player);
		bool hasMultiple = !IsSelfMenu && MenuCount() > 1;
		if (_holdContainer != null)
		{
			_holdContainer.Visible = hasMultiple && _player.CurInteractive == null;
		}
		if (_holdTimer != null)
		{
			_holdTimer.Value = 0;
		}
		UpdateIcon();
	}

	void UpdateIcon()
	{
		if (_icon == null)
		{
			return;
		}
		InteractiveAction action = GetActiveAction();
		_icon.Texture = action?.icon;
		_icon.Visible = action?.icon != null;
		// Tint the icon when the action's ingredient cost can't be met, so the block
		// reads before the press (which the runner's reagent gate refuses anyway).
		_icon.SelfModulate = CanAfford(action) ? Colors.White : _unaffordableTint;
	}

	// True when the action has no ingredient cost or the player's material pool
	// (backpack + party stash) can currently cover it. Mirrors the runner's press
	// gate (Player.HasReagents) so the visual and the refusal agree.
	bool CanAfford(InteractiveAction action)
	{
		return action == null || action.reagents.Count == 0 || _player.HasReagents(action.reagents);
	}

	InteractiveAction GetActiveAction()
	{
		// While the modal is open the focused option indexes the MERGED list (world +
		// self), so the big icon can preview a self-action too; otherwise the persistent
		// prompt tracks this interactive's own action at the committed/default index.
		if (_modalOpen && _modalFocusedIndex >= 0)
		{
			Array<InteractiveAction> menu = _menuActions;
			return (menu != null && _modalFocusedIndex < menu.Count) ? menu[_modalFocusedIndex] : null;
		}
		if (_actions == null || _actions.Count == 0)
		{
			return null;
		}
		int idx = _player.CurInteractive == _interactive ? _player.CurInteractiveActionIndex : 0;
		if (idx < 0 || idx >= _actions.Count)
		{
			return null;
		}
		return _actions[idx];
	}

	void Update()
	{
		// Hide while another fullscreen HUD (merchant, conversation, cooking,
		// etc.) has set InputSuppressed — our own options modal is the one
		// case where we set InputSuppressed and still want to be visible.
		GameClient gc = GameClient.Current;
		bool externalHudActive = gc != null && gc.InputSuppressed && !_modalOpen;
		Vector3 worldPosition = _interactive.hudPosition;
		if (externalHudActive || _camera.IsPositionBehind(worldPosition))
		{
			Visible = false;
			return;
		}

		Visible = true;
		Position = GameClient.Current.ProjectToScreen(worldPosition);

		// Half-opaque while the player is only standing next to the
		// interactive (preview); full-opacity once they commit (action in
		// flight) or while picking an option in the modal.
		bool committed = _modalOpen || _player.CurInteractive == _interactive;
		Modulate = new Color(1f, 1f, 1f, committed ? 1f : 0.5f);

		// The set of actions can change at runtime (e.g. campfire toggles
		// between [Cook,Douse] and [Light] when lit/unlit). Refresh each
		// frame so the icon and hold-bar visibility stay correct without
		// every interactive needing to push a signal.
		Array<InteractiveAction> latest = _interactive.GetActions(_player);
		if (!ReferenceEquals(latest, _actions))
		{
			_actions = latest;
			bool hasMultiple = !IsSelfMenu && MenuCount() > 1;
			if (_holdContainer != null && !_modalOpen)
			{
				_holdContainer.Visible = hasMultiple && _player.CurInteractive == null;
			}
		}

		UpdateIcon();

		// Pips ride with the icon: shown only while an action icon is up and the
		// interactive carries a level. Parent Visible already gated on-screen above.
		if (_levelContainer != null)
		{
			_levelContainer.Visible = _interactLevel > 0 && _icon != null && _icon.Visible;
		}

		if (_interactTimer != null)
		{
			_interactTimer.Value = _player.ClientInteractProgress;
		}

		if (!_modalOpen)
		{
			bool hasMultiple = !IsSelfMenu && MenuCount() > 1;
			if (_holdContainer != null)
			{
				_holdContainer.Visible = hasMultiple && _player.CurInteractive == null;
			}
			if (_holdTimer != null)
			{
				_holdTimer.Value = _player.InteractHoldProgress;
			}
		}
	}

	public override void _Process(double delta)
	{
		using var _prof = Profiler.Sample("InteractHUD.Process");

		Update();
	}

	void OnInteractMenuOpenRequested()
	{
		// Only respond if we're the HUD for the player's current highlight —
		// a different interactive's HUD shouldn't open its modal in response.
		if (_player.HighlightInteractive != _interactive)
		{
			return;
		}
		OpenModal();
	}

	void OpenModal()
	{
		if (_modalOpen || _actions == null || _actions.Count == 0)
		{
			return;
		}
		_modalOpen = true;
		_modalFocusedIndex = -1;
		if (_holdContainer != null)
		{
			_holdContainer.Visible = false;
		}
		if (_interactOptionsParent != null)
		{
			_interactOptionsParent.Visible = true;
		}
		PopulateOptions();
		GameClient gc = GameClient.Current;
		if (gc != null)
		{
			gc.InputSuppressed = true;
		}
	}

	void PopulateOptions()
	{
		if (_interactOptionsContainer == null || _interactOptionScene == null)
		{
			return;
		}
		// Drop any leftovers from a prior open.
		foreach (Node child in _interactOptionsContainer.GetChildren())
		{
			child.QueueFree();
		}
		// The options list is the MERGED menu: this interactive's actions followed by
		// the player's always-available self-actions (Pray, ...). Captured here so the
		// focused-option icon and the selection routing agree on indices.
		_menuActions = _player.GetMenuActions(_interactive);
		Button firstButton = null;
		for (int i = 0; i < _menuActions.Count; i++)
		{
			InteractiveAction action = _menuActions[i];
			if (action == null)
			{
				continue;
			}
			Button btn = _interactOptionScene.Instantiate<Button>();
			string label = action.displayName.ToString();
			btn.Text = string.IsNullOrEmpty(label) ? action.verb.ToString() : label;
			// Dim (but leave selectable) an option the player can't afford — picking
			// it still fires the runner's reject cue + "not enough ingredients" line.
			if (!CanAfford(action))
			{
				btn.Modulate = _unaffordableTint;
			}
			int idx = i;
			btn.Pressed += () => OnOptionSelected(idx);
			btn.FocusEntered += () => _modalFocusedIndex = idx;
			btn.MouseEntered += () => btn.GrabFocus();
			_interactOptionsContainer.AddChild(btn);
			if (firstButton == null)
			{
				firstButton = btn;
			}
		}
		firstButton?.CallDeferred(Control.MethodName.GrabFocus);
	}

	void OnOptionSelected(int index)
	{
		IInteractive interactive = _interactive;
		Player player = _player;
		CloseModal();
		// Route through the merged menu: TryStartMenuAction sends the first worldCount
		// indices to the world interactive and the rest to a self-action.
		if (interactive != null && player != null && interactive.CanActorInteract(player))
		{
			player.TryStartMenuAction(interactive, index);
		}
	}

	void CloseModal()
	{
		if (!_modalOpen)
		{
			return;
		}
		_modalOpen = false;
		_modalFocusedIndex = -1;
		_menuActions = null;
		if (_interactOptionsParent != null)
		{
			_interactOptionsParent.Visible = false;
		}
		if (_interactOptionsContainer != null)
		{
			foreach (Node child in _interactOptionsContainer.GetChildren())
			{
				child.QueueFree();
			}
		}
		bool hasMultiple = !IsSelfMenu && MenuCount() > 1;
		if (_holdContainer != null)
		{
			_holdContainer.Visible = hasMultiple && _player.CurInteractive == null;
		}
		_player?.CloseInteractMenu();
		GameClient gc = GameClient.Current;
		if (gc != null)
		{
			gc.InputSuppressed = false;
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!_modalOpen)
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel"))
		{
			CloseModal();
			GetViewport().SetInputAsHandled();
		}
	}
}
