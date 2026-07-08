using System;
using Godot;

// One selectable forge choice: an ItemInfoPanel display of the offered item with
// a full-card Button overlaid on top to catch the pick (focus + press). Mirrors
// UpgradePanel's PanelContainer-plus-Button shape, but reuses ItemInfoPanel for
// the item readout instead of authoring bespoke labels.
[GlobalClass]
public partial class ForgeItemCard : PanelContainer
{
    [Export] private ItemInfoPanel _infoPanel;
    [Export] private Button _button;

    Action _onChosen;

    public void Init(ItemState item, Action onChosen)
    {
        _onChosen = onChosen;
        // Forged gear is always shown fully identified so the player can compare
        // stats before committing.
        _infoPanel?.SetItem(item, forceIdentified: true);
    }

    public override void _Ready()
    {
        if (_button != null)
        {
            _button.Pressed += OnPressed;
        }
    }

    public override void _ExitTree()
    {
        if (_button != null)
        {
            _button.Pressed -= OnPressed;
        }
    }

    public void GrabButtonFocus()
    {
        _button?.GrabFocus();
    }

    void OnPressed()
    {
        _onChosen?.Invoke();
    }
}
