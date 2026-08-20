using Godot;

// Status HUD for the world-map painter. Exported labels assigned in the scene;
// the painter pushes the active tool's name, parameters, brush size, and the
// 2D/3D view state.
[GlobalClass]
public partial class WorldMapHud : CanvasLayer
{
    [Export] public Label viewLabel;
    [Export] public Label layerLabel;   // tool name
    [Export] public Label toolLabel;    // tool status + active level
    [Export] public Label radiusLabel;
    [Export] public Label coordsLabel;
    [Export] public Label helpLabel;
    // Background-bake readout, bottom right. Hidden unless a bake is running or
    // has just finished.
    [Export] public Control bakePanel;
    [Export] public Label bakeLabel;
    [Export] public ProgressBar bakeBar;
    // Rows the tool buttons and the active tool's option buttons are built into.
    [Export] public Container toolButtonBar;
    [Export] public Container optionButtonBar;
    // Shortcut hints, global plus whatever the active tool adds.
    [Export] public Label hintLabel;

    private Button[] _toolButtons;
    private Button[] _optionButtons;

    public override void _Ready()
    {
    }

    // Both bars are built from lists the painter hands over rather than authored
    // one-per-node, so adding a tool — or an op to a tool — cannot leave a stale
    // button behind. The OPTION row labels its hotkeys (1..9), because that is
    // the thing you change mid-stroke; switching tool is Tab or a click.
    public void BuildToolButtons(string[] names, System.Action<int> onPressed)
    {
        _toolButtons = BuildGroup(toolButtonBar, names, onPressed, false);
    }

    // Called again on every tool change, so it clears whatever the last tool put
    // there. A tool with no discrete options leaves the row empty.
    public void BuildOptionButtons(string[] names, Color[] colors, System.Action<int> onPressed)
    {
        _optionButtons = BuildGroup(optionButtonBar, names, onPressed, true, colors);
    }

    public void SetActiveTool(int index)
    {
        SetActive(_toolButtons, index);
    }

    public void SetActiveOption(int index)
    {
        SetActive(_optionButtons, index);
    }

    private static Button[] BuildGroup(Container bar, string[] names, System.Action<int> onPressed, bool numbered, Color[] colors = null)
    {
        if (bar == null)
        {
            return null;
        }
        foreach (Node child in bar.GetChildren())
        {
            // Detach now rather than waiting for the free: QueueFree alone would
            // leave the old row on screen alongside the new one for a frame.
            bar.RemoveChild(child);
            child.QueueFree();
        }
        var group = new ButtonGroup();
        var buttons = new Button[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Text = numbered ? $"{i + 1}  {names[i]}" : names[i],
                ToggleMode = true,
                ButtonGroup = group,
                // Never take keyboard focus: the painter's shortcuts are bare
                // keys, and a focused button would eat them.
                FocusMode = Control.FocusModeEnum.None,
            };
            if (colors != null && i < colors.Length)
            {
                // The label carries the swatch: same colour the map draws this
                // option in, so the two cannot drift.
                button.AddThemeColorOverride("font_color", colors[i]);
                button.AddThemeColorOverride("font_pressed_color", colors[i]);
                button.AddThemeColorOverride("font_hover_color", colors[i]);
            }
            button.Pressed += () => onPressed(index);
            bar.AddChild(button);
            buttons[i] = button;
        }
        return buttons;
    }

    private static void SetActive(Button[] buttons, int index)
    {
        if (buttons == null)
        {
            return;
        }
        for (int i = 0; i < buttons.Length; i++)
        {
            // No-signal: this is reflecting a selection, not making one, and the
            // plain setter would call straight back into the painter.
            buttons[i].SetPressedNoSignal(i == index);
        }
    }

    public void SetHint(string hint)
    {
        if (hintLabel != null)
        {
            hintLabel.Text = hint;
        }
    }

    public void SetView(bool preview)
    {
        if (viewLabel != null)
        {
            viewLabel.Text = preview ? "View: 3D Preview" : "View: 2D Map";
        }
    }

    public void SetTool(string name)
    {
        if (layerLabel != null)
        {
            layerLabel.Text = $"Tool: {name}";
        }
    }

    public void SetStatus(string status)
    {
        if (toolLabel != null)
        {
            toolLabel.Text = status;
        }
    }

    public void SetRadius(float radius, int pixelsPerMeter)
    {
        if (radiusLabel != null)
        {
            radiusLabel.Text = $"Brush: {radius:F1}m   Zoom: {pixelsPerMeter}px/m";
        }
    }

    public void SetBakeProgress(bool active, float ratio, string text)
    {
        if (bakePanel != null)
        {
            bakePanel.Visible = active;
        }
        if (bakeBar != null)
        {
            bakeBar.Value = ratio;
        }
        if (bakeLabel != null)
        {
            bakeLabel.Text = text;
        }
    }

    // Height under the cursor as well as position: judging elevation by colour
    // alone is guesswork, and the number is what the author is actually aiming.
    // Water is called out only where it actually stands over the ground, since
    // "my land is flat at +2 and still submerged" is unreadable from colour.
    public void SetCoords(Vector2I texel, int worldY, int level, int waterY)
    {
        if (coordsLabel == null)
        {
            return;
        }
        string water = waterY > worldY ? $"   WATER Y={waterY} (depth {waterY - worldY})" : "";
        coordsLabel.Text = $"Texel: ({texel.X}, {texel.Y})   Y={worldY}  (level {level:+#;-#;0}){water}";
    }
}
