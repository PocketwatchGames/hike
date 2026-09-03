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
    // Names whatever the cursor is over that the map can only draw as a mark —
    // a hand-placed entity. Blank, not hidden: it sits in a VBox and hiding it
    // would shuffle the rows under it every time the cursor crossed a chest.
    [Export] public Label hoverLabel;
    [Export] public Label helpLabel;
    // Background-bake readout, bottom right. Hidden unless a bake is running or
    // has just finished.
    [Export] public Control bakePanel;
    [Export] public Label bakeLabel;
    [Export] public ProgressBar bakeBar;
    // Row the tool buttons are built into.
    [Export] public Container toolButtonBar;
    // The active tool's options. An ItemList rather than a row of buttons: a
    // palette runs to 20+ entries (the entity palette, every .hikescene), which
    // a wrapping button row turned into a block of the screen that grew with the
    // content. A list has a fixed footprint and scrolls instead.
    [Export] public ItemList optionList;
    // Shortcut hints, global plus whatever the active tool adds.
    [Export] public Label hintLabel;
    // Properties of the entity tool's selection. Hides itself when there is no
    // selection, so it costs nothing on the other tools.
    [Export] public WorldMapEntityInspector entityInspector;

    private Button[] _toolButtons;
    // Held rather than re-connected: BuildOptionButtons runs on every tool change
    // with a new callback, and connecting there would stack a handler per change.
    // One connection made in _Ready dispatches through this instead.
    private System.Action<int> _onOptionPressed;

    public override void _Ready()
    {
        if (optionList != null)
        {
            // Never take keyboard focus — the painter's shortcuts are bare keys
            // (1-9, Q/E, W, X, Tab), and a focused list would eat them for its
            // own navigation. Mouse selection and wheel scrolling do not need
            // focus, so the list still behaves.
            optionList.FocusMode = Control.FocusModeEnum.None;
            optionList.ItemSelected += index => _onOptionPressed?.Invoke((int)index);
        }
    }

    // Both bars are built from lists the painter hands over rather than authored
    // one-per-node, so adding a tool — or an op to a tool — cannot leave a stale
    // button behind. The OPTION row labels its hotkeys (1..9), because that is
    // the thing you change mid-stroke; switching tool is Tab or a click.
    public void BuildToolButtons(string[] names, System.Action<int> onPressed)
    {
        _toolButtons = BuildGroup(toolButtonBar, names, onPressed);
    }

    // Called again on every tool change, so it clears whatever the last tool put
    // there. A tool with no discrete options leaves the list empty.
    public void BuildOptionButtons(string[] names, Color[] colors, System.Action<int> onPressed)
    {
        _onOptionPressed = onPressed;
        if (optionList == null)
        {
            return;
        }
        optionList.Clear();
        for (int i = 0; i < names.Length; i++)
        {
            // 1-9 pick an option, so only the first nine can name a key that
            // does anything; the rest are clicked.
            optionList.AddItem(i < NUMBER_KEYS ? $"{i + 1}  {names[i]}" : names[i]);
            if (colors != null && i < colors.Length)
            {
                // Same colour the map draws this option in, so the two cannot
                // drift — lifted only as far as the list's dark panel needs.
                optionList.SetItemCustomFgColor(i, Legible(colors[i]));
            }
        }
    }

    public void SetActiveTool(int index)
    {
        SetActive(_toolButtons, index);
    }

    // Reflects a selection rather than making one. ItemList.Select does not emit
    // ItemSelected, so this cannot call back into the painter — the same reason
    // the tool buttons use SetPressedNoSignal.
    public void SetActiveOption(int index)
    {
        if (optionList == null)
        {
            return;
        }
        if (index < 0 || index >= optionList.ItemCount)
        {
            optionList.DeselectAll();
            return;
        }
        optionList.Select(index);
        // A long palette scrolls, so the active entry can be off-screen after a
        // Q/E step or a tool change that restores a stored index.
        optionList.EnsureCurrentIsVisible();
    }

    // 1-9 pick an option, so a list longer than nine cannot label the rest with a
    // key that does nothing. The overflow is clicked instead.
    private const int NUMBER_KEYS = 9;

    // Dimmest a swatch may be as TEXT on the list's dark panel. The map colours
    // are authored to read as washes against each other, not as glyphs against
    // black — region 0 is near-black by design — so anything under this is
    // brightened toward white until it is, keeping its hue.
    private const float MIN_LABEL_LUMA = 0.45f;

    private static Color Legible(Color c)
    {
        float luma = 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
        if (luma >= MIN_LABEL_LUMA)
        {
            return c;
        }
        return c.Lerp(Colors.White, (MIN_LABEL_LUMA - luma) / (1f - luma));
    }

    private static Button[] BuildGroup(Container bar, string[] names, System.Action<int> onPressed)
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
                Text = names[i],
                ToggleMode = true,
                ButtonGroup = group,
                // Never take keyboard focus: the painter's shortcuts are bare
                // keys, and a focused button would eat them.
                FocusMode = Control.FocusModeEnum.None,
            };
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

    // What the cursor is over, or "" for nothing. A mark is one metre and every
    // entity draws the same dot, so the map can say one is THERE but never what
    // it is.
    public void SetHovered(string name)
    {
        if (hoverLabel != null)
        {
            hoverLabel.Text = string.IsNullOrEmpty(name) ? "" : $"Entity: {name}";
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
    // Water is reported even where the ground hides it: a column can hold water
    // BELOW its surface, which is invisible on the map by design and becomes a
    // lake the moment the land above it is carved away. Without this the author
    // has no way to find water they painted under a hill.
    public void SetCoords(Vector2I texel, int worldY, int level, int waterY, bool hasWater)
    {
        if (coordsLabel == null)
        {
            return;
        }
        string water = waterY > worldY ? $"   WATER Y={waterY} (depth {waterY - worldY})"
            : hasWater ? $"   water Y={waterY} (buried)"
            : "   no water";
        coordsLabel.Text = $"Texel: ({texel.X}, {texel.Y})   Y={worldY}  (level {level:+#;-#;0}){water}";
    }
}
