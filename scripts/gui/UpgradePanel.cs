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
		}
	}

	public override void _ExitTree()
	{
		if (_upgradeButton != null)
		{
			_upgradeButton.Pressed -= OnUpgradePressed;
		}
	}

	// Lets UpgradeScreen put gamepad/keyboard focus on the first card's button
	// so the screen is navigable without a mouse.
	public void GrabButtonFocus()
	{
		_upgradeButton?.GrabFocus();
	}

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
