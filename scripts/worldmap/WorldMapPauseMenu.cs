using System;
using Godot;

// Escape menu for the world-map painter. A full-rect Control so it swallows the
// mouse before the canvas under it — the painter must not paint through an open
// menu — with the button column centred inside it.
//
// Purely a panel: the actions are Actions the painter assigns, so the same
// buttons drive the same code Ctrl+S and the quit path already use.
[GlobalClass]
public partial class WorldMapPauseMenu : Control
{
    [Export] public Label versionLabel;

    public Action onSave;
    public Action onResume;
    public Action onQuit;

    public override void _Ready()
    {
        Visible = false;
        if (versionLabel != null)
        {
            versionLabel.Text = Version.Display;
        }
    }

    public void SetOpen(bool open)
    {
        Visible = open;
    }

    public void OnSaveButtonPressed()
    {
        onSave?.Invoke();
    }

    public void OnResumeButtonPressed()
    {
        onResume?.Invoke();
    }

    public void OnQuitButtonPressed()
    {
        onQuit?.Invoke();
    }
}
