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
    // The active tool's options: a tree, so a tool whose options carry sections
    // (the entity palette, filed by folder) shows them as collapsible groups,
    // and one without shows a flat list in the same fixed, scrolling footprint.
    [Export] public Tree optionTree;
    // Narrows the option tree by name or section. Shown only for a sectioned
    // list — the long, palette-backed ones.
    [Export] public LineEdit optionFilter;
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

    // What the current option tree was built from, kept so the filter can
    // rebuild it.
    private string[] _optionNames = System.Array.Empty<string>();
    private string[] _optionSections;
    private Color[] _optionColors;
    private bool _optionNumberKeys;
    private int _activeOption = -1;
    // Option index -> its row, or null while filtered out.
    private TreeItem[] _optionItems = System.Array.Empty<TreeItem>();
    // Section paths the author has opened. Sections start closed; the active
    // option's section opens itself.
    private readonly System.Collections.Generic.HashSet<string> _openSections = new();
    // Set while the HUD itself selects or collapses rows, whose signals must not
    // read as the author's choice.
    private bool _syncingTree;

    public override void _Ready()
    {
        if (optionTree != null)
        {
            // Never take keyboard focus — the painter's shortcuts are bare keys
            // (1-9, Q/E, W, X, Tab), and a focused tree would eat them for its
            // own navigation. Mouse selection and wheel scrolling do not need it.
            optionTree.FocusMode = Control.FocusModeEnum.None;
            optionTree.HideRoot = true;
            optionTree.ItemSelected += OnOptionRowSelected;
            optionTree.ItemCollapsed += OnSectionToggled;
        }
        if (optionFilter != null)
        {
            optionFilter.TextChanged += _ => RebuildOptionTree();
            optionFilter.TextSubmitted += _ => PickFirstFiltered();
            optionFilter.GuiInput += OnFilterInput;
        }
    }

    // Both bars are built from lists the painter hands over rather than authored
    // one-per-node, so adding a tool — or an op to a tool — cannot leave a stale
    // button behind. The OPTION list labels its hotkeys where the tool HAS them
    // (IWorldMapTool.NumberKeys), because that is the thing you change
    // mid-stroke; switching tool is Tab or a click.
    public void BuildToolButtons(string[] names, System.Action<int> onPressed)
    {
        _toolButtons = BuildGroup(toolButtonBar, names, onPressed);
    }

    // Called again on every tool change, so it clears whatever the last tool put
    // there. A tool with no discrete options leaves the list empty.
    // `numberKeys` is the tool's answer, not a count: a palette-backed tool has
    // no digits at all, however short its list happens to be today. `sections`
    // (one "A/B" path per option, "" for the top level) groups the list and
    // brings up the filter; null shows it flat.
    public void BuildOptionButtons(string[] names, Color[] colors, bool numberKeys,
        System.Action<int> onPressed, string[] sections = null)
    {
        _onOptionPressed = onPressed;
        _optionNames = names ?? System.Array.Empty<string>();
        _optionColors = colors;
        _optionNumberKeys = numberKeys;
        _optionSections = sections;
        _activeOption = -1;
        if (optionFilter != null)
        {
            optionFilter.Visible = sections != null;
            optionFilter.Text = "";
        }
        RebuildOptionTree();
    }

    public void SetActiveTool(int index)
    {
        SetActive(_toolButtons, index);
    }

    // Reflects a selection rather than making one, so it cannot call back into
    // the painter (the same reason the tool buttons use SetPressedNoSignal).
    // Opens the option's section and scrolls to it: a Q/E step or a tool change
    // that restores a stored index can land anywhere in a long list.
    public void SetActiveOption(int index)
    {
        _activeOption = index;
        if (optionTree == null)
        {
            return;
        }
        TreeItem item = index >= 0 && index < _optionItems.Length ? _optionItems[index] : null;
        _syncingTree = true;
        if (item == null)
        {
            optionTree.DeselectAll();
            _syncingTree = false;
            return;
        }
        for (TreeItem parent = item.GetParent(); parent != null && parent != optionTree.GetRoot(); parent = parent.GetParent())
        {
            parent.Collapsed = false;
            _openSections.Add(parent.GetMetadata(0).AsString());
        }
        item.Select(0);
        optionTree.ScrollToItem(item);
        _syncingTree = false;
    }

    // The painter calls this on any press over the map: the filter is the one
    // control here that takes keyboard focus, and the map canvas never does, so
    // without it a click back onto the map would leave every shortcut typing
    // into the filter.
    public void ReleaseOptionFilter()
    {
        if (optionFilter != null && optionFilter.HasFocus())
        {
            optionFilter.ReleaseFocus();
        }
    }

    private void RebuildOptionTree()
    {
        if (optionTree == null)
        {
            return;
        }
        string query = optionFilter?.Text?.StripEdges().ToLower() ?? "";
        bool filtering = query.Length > 0;
        _syncingTree = true;
        optionTree.Clear();
        TreeItem root = optionTree.CreateItem();
        var sectionRows = new System.Collections.Generic.Dictionary<string, TreeItem>();
        _optionItems = new TreeItem[_optionNames.Length];
        for (int i = 0; i < _optionNames.Length; i++)
        {
            string name = _optionNames[i];
            string section = _optionSections != null && i < _optionSections.Length ? _optionSections[i] ?? "" : "";
            if (filtering && !name.ToLower().Contains(query) && !section.ToLower().Contains(query))
            {
                continue;
            }
            TreeItem row = optionTree.CreateItem(SectionRow(root, section, sectionRows, filtering));
            // Only the first nine can name a key that does anything; the rest
            // are clicked, and a palette-backed tool labels none of them.
            row.SetText(0, _optionNumberKeys && i < NUMBER_KEYS ? $"{i + 1}  {name}" : name);
            row.SetMetadata(0, i);
            if (_optionColors != null && i < _optionColors.Length)
            {
                // Same colour the map draws this option in, so the two cannot
                // drift — lifted only as far as the list's dark panel needs.
                row.SetCustomColor(0, Legible(_optionColors[i]));
            }
            _optionItems[i] = row;
        }
        _syncingTree = false;
        SetActiveOption(_activeOption);
    }

    // The row for a section path, creating it and its parents on first use.
    // Every section is open while filtering, so a match is never hidden in a
    // closed group.
    private TreeItem SectionRow(TreeItem root, string section,
        System.Collections.Generic.Dictionary<string, TreeItem> rows, bool filtering)
    {
        if (section.Length == 0)
        {
            return root;
        }
        if (rows.TryGetValue(section, out TreeItem existing))
        {
            return existing;
        }
        int slash = section.LastIndexOf('/');
        TreeItem parent = slash < 0 ? root : SectionRow(root, section.Substring(0, slash), rows, filtering);
        TreeItem row = optionTree.CreateItem(parent);
        row.SetText(0, slash < 0 ? section : section.Substring(slash + 1));
        row.SetMetadata(0, section);
        row.Collapsed = !filtering && !_openSections.Contains(section);
        rows[section] = row;
        return row;
    }

    // A section row opens and shuts on a click, like its arrow; an option row
    // picks the option.
    private void OnOptionRowSelected()
    {
        if (_syncingTree)
        {
            return;
        }
        TreeItem item = optionTree.GetSelected();
        if (item == null)
        {
            return;
        }
        Variant meta = item.GetMetadata(0);
        if (meta.VariantType == Variant.Type.String)
        {
            item.Collapsed = !item.Collapsed;
            SetActiveOption(_activeOption);
            return;
        }
        _onOptionPressed?.Invoke(meta.AsInt32());
    }

    private void OnSectionToggled(TreeItem item)
    {
        if (_syncingTree || (optionFilter?.Text?.Length ?? 0) > 0)
        {
            return;
        }
        string section = item.GetMetadata(0).AsString();
        if (item.Collapsed)
        {
            _openSections.Remove(section);
        }
        else
        {
            _openSections.Add(section);
        }
    }

    // Enter takes the first match, so filter-then-Enter is a keyboard pick.
    private void PickFirstFiltered()
    {
        for (int i = 0; i < _optionItems.Length; i++)
        {
            if (_optionItems[i] != null)
            {
                _onOptionPressed?.Invoke(i);
                break;
            }
        }
        optionFilter.ReleaseFocus();
    }

    // Escape clears the filter, then a second press hands the keys back to the
    // painter.
    private void OnFilterInput(InputEvent e)
    {
        if (e is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        {
            if (optionFilter.Text.Length > 0)
            {
                optionFilter.Text = "";
                RebuildOptionTree();
            }
            else
            {
                optionFilter.ReleaseFocus();
            }
            optionFilter.AcceptEvent();
        }
    }

    // Where a tool has digits, only nine of its options can carry one. The
    // overflow is clicked instead.
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
