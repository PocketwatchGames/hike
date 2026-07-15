using Godot;
using System;

// One selectable boon card in the UpgradeScreen. Shows a status effect's
// name, icon, flavor text, and a per-dial stat readout (reusing the same
// StatList.StatusEffectInfo generator the inventory tooltip uses), with a
// button that commits the choice. UpgradeScreen instances one per offered
// upgrade and wires the button callback back to itself.
[GlobalClass]
public partial class UpgradePanel : PanelContainer
{
	[Export] private Label _titleLabel;
	[Export] private Label _descriptionLabel;
	[Export] private TextureRect _iconTextureRect;
	[Export] private Button _upgradeButton;
	[Export] private Container _statContainer;
	[Export] private PackedScene _statScene;

	// Card tint while its button holds / lacks gamepad focus. The focused card
	// shows at full brightness; the rest dim, so the selection is obvious without
	// a mouse. Modulate is used (not self_modulate) so the whole card dims, not
	// just the panel background — independent of whatever theme stylebox is set.
	[Export] private Color _focusedModulate = Colors.White;
	[Export] private Color _unfocusedModulate = new Color(0.5f, 0.5f, 0.5f, 1f);

	// Fired when this card's button is pressed; UpgradeScreen passes a closure
	// that applies the bound effect and closes the modal.
	Action _onChosen;

	public void Init(BoonData upgrade, Action onChosen)
	{
		_onChosen = onChosen;
		if (_titleLabel != null)
		{
			string name = upgrade?.DisplayName.ToString();
			if (string.IsNullOrEmpty(name))
			{
				name = upgrade?.ResourceName ?? string.Empty;
			}
			_titleLabel.Text = name;
		}
		if (_descriptionLabel != null)
		{
			_descriptionLabel.Text = upgrade?.Description ?? string.Empty;
		}
		if (_iconTextureRect != null)
		{
			_iconTextureRect.Texture = upgrade?.Icon;
		}
		PopulateStats(upgrade?.statusEffect);
	}

	public override void _Ready()
	{
		if (_upgradeButton != null)
		{
			_upgradeButton.Pressed += OnUpgradePressed;
			_upgradeButton.FocusEntered += OnFocusEntered;
			_upgradeButton.FocusExited += OnFocusExited;
		}
		// Start dimmed; whichever card grabs focus brightens itself.
		Modulate = _unfocusedModulate;
	}

	public override void _ExitTree()
	{
		if (_upgradeButton != null)
		{
			_upgradeButton.Pressed -= OnUpgradePressed;
			_upgradeButton.FocusEntered -= OnFocusEntered;
			_upgradeButton.FocusExited -= OnFocusExited;
		}
	}

	// Lets UpgradeScreen put gamepad/keyboard focus on the first card's button
	// so the screen is navigable without a mouse.
	public void GrabButtonFocus()
	{
		_upgradeButton?.GrabFocus();
	}

	// Wire this card's left/right gamepad-focus neighbors to the adjacent cards
	// (either may be null at the ends). Explicit so navigation is guaranteed and
	// doesn't depend on Godot's geometry-based auto-neighbor resolution.
	public void SetFocusNeighbors(UpgradePanel left, UpgradePanel right)
	{
		if (_upgradeButton == null)
		{
			return;
		}
		if (left?._upgradeButton != null)
		{
			_upgradeButton.FocusNeighborLeft = _upgradeButton.GetPathTo(left._upgradeButton);
		}
		if (right?._upgradeButton != null)
		{
			_upgradeButton.FocusNeighborRight = _upgradeButton.GetPathTo(right._upgradeButton);
		}
	}

	void OnFocusEntered() => Modulate = _focusedModulate;
	void OnFocusExited() => Modulate = _unfocusedModulate;

	void OnUpgradePressed()
	{
		_onChosen?.Invoke();
	}

	// One StatPanel per dial the effect moves — duration, dps/heal, and every
	// StatModifier entry — via the shared StatList generator.
	void PopulateStats(StatusEffectData upgrade)
	{
		if (_statContainer == null)
		{
			return;
		}
		foreach (Node child in _statContainer.GetChildren())
		{
			if (child is StatPanel)
			{
				child.QueueFree();
			}
		}
		if (upgrade == null || _statScene == null)
		{
			return;
		}
		foreach (var (name, value) in StatList.StatusEffectInfo(upgrade))
		{
			StatPanel stat = _statScene.Instantiate<StatPanel>();
			_statContainer.AddChild(stat);
			stat.SetText(name, value);
		}
	}
}
