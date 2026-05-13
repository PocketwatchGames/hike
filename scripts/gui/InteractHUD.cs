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

	Camera3D _camera;
	Player _player;
	IInteractive _interactive;
	Array<InteractiveAction> _actions;
	bool _modalOpen;

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
		Update();
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
		bool hasMultiple = _actions != null && _actions.Count > 1;
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
	}

	InteractiveAction GetActiveAction()
	{
		if (_actions == null || _actions.Count == 0)
		{
			return null;
		}
		int idx = (_player.CurInteractive == _interactive) ? _player.CurInteractiveActionIndex : 0;
		if (idx < 0 || idx >= _actions.Count)
		{
			return null;
		}
		return _actions[idx];
	}

	void Update()
	{
		Vector3 worldPosition = _interactive.hudPosition;
		if (_camera.IsPositionBehind(worldPosition))
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
			bool hasMultiple = _actions != null && _actions.Count > 1;
			if (_holdContainer != null && !_modalOpen)
			{
				_holdContainer.Visible = hasMultiple && _player.CurInteractive == null;
			}
		}

		UpdateIcon();

		if (_interactTimer != null)
		{
			_interactTimer.Value = _player.ClientInteractProgress;
		}

		if (!_modalOpen)
		{
			bool hasMultiple = _actions != null && _actions.Count > 1;
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
		Button firstButton = null;
		for (int i = 0; i < _actions.Count; i++)
		{
			InteractiveAction action = _actions[i];
			if (action == null)
			{
				continue;
			}
			Button btn = _interactOptionScene.Instantiate<Button>();
			string label = action.displayName.ToString();
			btn.Text = string.IsNullOrEmpty(label) ? action.verb.ToString() : label;
			btn.Icon = action.icon;
			int idx = i;
			btn.Pressed += () => OnOptionSelected(idx);
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
		if (interactive != null && player != null && interactive.CanActorInteract(player))
		{
			player.TryStartInteractiveAction(interactive, index);
		}
	}

	void CloseModal()
	{
		if (!_modalOpen)
		{
			return;
		}
		_modalOpen = false;
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
		bool hasMultiple = _actions != null && _actions.Count > 1;
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
