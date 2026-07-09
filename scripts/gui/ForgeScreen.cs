using System;
using Godot;

// "Forge an upgrade" modal. Opened by GameClient when the player uses a forge.
// Shows a single offered upgrade on the right (via StatusEffectInfoPanel) and the
// upgrade it would replace in that slot on the left (or an "empty slot" note when
// the slot is free), plus the forge's level as a row of stars. The player accepts
// (ui_accept) or backs out (ui_cancel); GameClient owns visibility and the input /
// HUD / mouse gating. This class is purely the view + accept/cancel plumbing.
[GlobalClass]
public partial class ForgeScreen : Control
{
    // Left: the upgrade currently in the slot (what gets replaced). Right: the
    // forge's offer.
    [Export] private StatusEffectInfoPanel _replacingPanel;
    [Export] private StatusEffectInfoPanel _offeredPanel;
    // Shown on the left when the slot is empty (nothing to replace).
    [Export] private Control _replacingEmptyLabel;
    // Up to five star pips lit to the forge's level.
    [Export] private Godot.Collections.Array<TextureRect> _levelStars = new();

    // Invoked when the player accepts the offered upgrade; GameClient applies it
    // and closes the screen.
    Action _onAccept;
    // Invoked instead when the player backs out.
    Action _onCancel;

    public void Init(Action acceptFunc, Action onCancel, StatusEffectData offered, StatusEffectData replacing, int level)
    {
        _onAccept = acceptFunc;
        _onCancel = onCancel;

        if (_offeredPanel != null && offered != null)
        {
            _offeredPanel.SetStatusEffect(offered);
        }

        bool hasReplacing = replacing != null;
        if (_replacingPanel != null)
        {
            _replacingPanel.Visible = hasReplacing;
            if (hasReplacing)
            {
                _replacingPanel.SetStatusEffect(replacing);
            }
        }
        if (_replacingEmptyLabel != null)
        {
            _replacingEmptyLabel.Visible = !hasReplacing;
        }

        for (int i = 0; i < _levelStars.Count; i++)
        {
            if (_levelStars[i] != null)
            {
                _levelStars[i].Visible = i < level;
            }
        }
    }

    void Accept()
    {
        Action cb = _onAccept;
        _onAccept = null;
        _onCancel = null;
        cb?.Invoke();
    }

    void Cancel()
    {
        Action cb = _onCancel;
        _onAccept = null;
        _onCancel = null;
        cb?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible)
        {
            return;
        }
        if (e.IsActionPressed("ui_cancel"))
        {
            Cancel();
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed("ui_accept"))
        {
            Accept();
            GetViewport().SetInputAsHandled();
        }
    }
}
