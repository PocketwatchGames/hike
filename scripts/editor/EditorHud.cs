using System;
using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class EditorHud : CanvasLayer
{
    [Export] public Label clipLabel;
    [Export] public Label coordsLabel;
    [Export] public Label helpLabel;
    // What Ctrl+S will write. Always visible, so a save that goes somewhere
    // unexpected is obvious before it happens rather than after.
    [Export] public Label documentLabel;
    // Transient save feedback. Hidden until a save reports in.
    [Export] public Label toastLabel;
    [Export] public Color toastSuccessColor = new Color(0.6f, 1f, 0.6f);
    [Export] public Color toastFailureColor = new Color(1f, 0.5f, 0.45f);
    [Export(PropertyHint.Range, "0.5,10,0.1")] public float toastSeconds = 3f;

    [ExportGroup("Tool Palette")]
    [Export] public PackedScene toolButtonScene;
    // Which brush a click paints with. A ButtonGroup in the scene keeps these
    // exclusive; Voxel is the one pressed at startup.
    [Export] public Button voxelToolButton;
    [Export] public Button entityToolButton;
    [Export] public Button roofToolButton;
    // Bottom-of-screen palettes. Exactly one is shown, per the selected tool.
    [Export] public Control voxelPalette;
    [Export] public Control entityPalette;
    [Export] public Control roofPalette;
    // Operation / shape / brush-summary panel. Voxel-only — none of it applies
    // to entity placement, so it hides with the voxel palette.
    [Export] public Control voxelInfoPanel;
    [Export] public GridContainer voxelTab;

    [ExportGroup("Entity Details")]
    // The entity tool's own panel — mode row plus selection readout. Mirrors
    // VoxelInfo: shown only while its tool is the active one.
    [Export] public Control entityDetailsPanel;
    [Export] public Button placeModeButton;
    [Export] public Button selectModeButton;
    [Export] public Label selectionLabel;
    // Placement / gizmo snapping. Entity-tool only, so they live on its panel.
    [Export] public CheckButton snapToGridButton;
    [Export] public CheckButton snapRotationButton;

    [ExportGroup("Roof Details")]
    // The roof tool's own panel — seam axis plus pitch. Mirrors VoxelInfo and
    // EntityDetails: shown only while its tool is the active one.
    [Export] public Control roofInfoPanel;
    [Export] public GridContainer roofTab;
    [Export] public Button ridgeXButton;
    [Export] public Button ridgeZButton;
    // Gable ends or hipped ends. A hip ignores the ridge direction — it derives
    // the seam from whichever footprint axis is longer.
    [Export] public Button gableFormButton;
    [Export] public Button hipFormButton;
    // Roof pitch in degrees. Range/step are authored on the slider itself.
    [Export] public HSlider slopeSlider;
    [Export] public Label slopeLabel;
    // How derelict the next roof is. Per-roof, not per-style.
    [Export] public HSlider brokenSlider;
    [Export] public Label brokenLabel;
    // Draw a new roof, or retune one already placed.
    [Export] public Button drawRoofModeButton;
    [Export] public Button editRoofModeButton;

    [ExportGroup("Entity Tabs")]
    // One container per EEditorEntityTab, in enum order.
    [Export] public Container interactivesTab;
    [Export] public Container treesTab;
    [Export] public Container rocksTab;
    [Export] public Container natureTab;
    [Export] public Container furnitureTab;
    [Export] public Container propsTab;

    [ExportGroup("Current Tool")]
    [Export] public TextureRect brushImage;
    [Export] public Label brushNameLabel;
    [Export] public Button paintButton;
    [Export] public Button eraseButton;
    [Export] public Button replaceButton;
    [Export] public Button voxelShapeButton;
    [Export] public Button floorShapeButton;
    [Export] public Button wallShapeButton;
    [Export] public Button fillShapeButton;
    [Export] public Button roomShapeButton;
    [Export] public Button windowShapeButton;
    [Export] public Button doorShapeButton;
    [Export] public Button plateauSnapButton;
    // Per-voxel edge shaping. Auto is the one pressed at startup.
    [Export] public Button autoEdgesButton;
    [Export] public Button blockyEdgesButton;
    [Export] public Button steppedEdgesButton;
    [Export] public Button smoothEdgesButton;

    [ExportGroup("View")]
    [Export] public CheckButton lightingButton;
    // Normalized awake-day clock: 0 = sunrise, 1/3 = noon, 2/3 = sunset,
    // 1 = midnight. Range/step are authored on the slider itself.
    [Export] public HSlider timeOfDaySlider;
    // Items are filled from the brush palette's presets at startup, so the
    // dropdown is authored in the .tres rather than in the scene.
    [Export] public OptionButton weatherOption;
    // Space class stamped into a subscene's enclosed cells when it is saved.
    // Filled from SimData.interiorAmbiences at startup.
    [Export] public OptionButton interiorClassOption;

    public Action<int> onVoxelBrushSelected;
    public Action<int> onEntityBrushSelected;
    public Action<int> onRoofBrushSelected;
    public Action<EEditorTool> onToolChanged;
    public Action<ERoofSeamAxis> onRoofSeamAxisChanged;
    public Action<ERoofForm> onRoofFormChanged;
    public Action<float> onRoofSlopeChanged;
    public Action<float> onRoofBrokenChanged;
    public Action<EEditorRoofMode> onRoofModeChanged;
    // A roof setting has settled and should be pushed onto the roof being
    // retuned. Every discrete change fires it; a slider fires on release rather
    // than on each value it streams through, because applying rebuilds the
    // roof's chunk and relights the world — far too much to do per drag pixel.
    public Action onRoofSettingsCommitted;
    public Action<EEditorEntityMode> onEntityToolModeChanged;
    public Action<bool> onSnapToGridChanged;
    public Action<bool> onSnapRotationChanged;
    public Action<EEditorBrushOperation> onOperationSelected;
    public Action<EEditorBrushShape> onShapeSelected;
    public Action<EEditorVoxelEdges> onVoxelEdgesSelected;
    public Action<bool> onPlateauSnapChanged;
    public Action<bool> onLightingChanged;
    public Action<float> onTimeOfDayChanged;
    public Action<int> onWeatherSelected;
    // Palette index, or -1 for "preserve whatever the cells already carry".
    public Action<int> onInteriorClassSelected;

    // Index-aligned with the brush entries. A slot is null when its tab wasn't
    // wired in the scene, so the palette index a brush selection carries stays
    // meaningful either way.
    private readonly List<EditorToolButton> _voxelButtons = new List<EditorToolButton>();
    private readonly List<EditorToolButton> _entityButtons = new List<EditorToolButton>();
    private readonly List<EditorToolButton> _roofButtons = new List<EditorToolButton>();
    private EditorBrushEntry[] _voxelEntries = Array.Empty<EditorBrushEntry>();
    private EditorBrushEntry[] _entityEntries = Array.Empty<EditorBrushEntry>();
    private EditorBrushEntry[] _roofEntries = Array.Empty<EditorBrushEntry>();

    private EEditorTool _tool = EEditorTool.Voxel;
    private EEditorEntityMode _entityToolMode = EEditorEntityMode.Place;

    // The persistent operation chosen on the panel. A held Ctrl / Alt shows its
    // operation pressed without touching this, so releasing the modifier snaps
    // the row back to what was chosen.
    private EEditorBrushOperation _operation = EEditorBrushOperation.Paint;
    private EEditorBrushOperation? _heldOverride;

    private float _toastRemaining;

    public override void _Ready()
    {
        if (helpLabel != null)
        {
            helpLabel.Text = "LMB: Paint | Ctrl+LMB: Erase | Alt+LMB: Replace | RMB: Fly (WASD/E/Q, Shift boost, Wheel speed) | R/F: Clip+Build Up/Down (Shift: 1m) | Z/C: Rotate | Ctrl+Z/Y: Undo/Redo | Ctrl+S: Save | Ctrl+Shift+S: Save As | Esc: Quit";
        }
        if (toastLabel != null)
        {
            toastLabel.Visible = false;
        }

        BindToolButton(voxelToolButton, EEditorTool.Voxel);
        BindToolButton(entityToolButton, EEditorTool.Entity);
        BindToolButton(roofToolButton, EEditorTool.Roof);
        BindEntityModeButton(placeModeButton, EEditorEntityMode.Place);
        BindEntityModeButton(selectModeButton, EEditorEntityMode.Select);
        BindRoofModeButton(drawRoofModeButton, EEditorRoofMode.Draw);
        BindRoofModeButton(editRoofModeButton, EEditorRoofMode.Edit);
        BindSeamAxisButton(ridgeXButton, ERoofSeamAxis.AlongX);
        BindSeamAxisButton(ridgeZButton, ERoofSeamAxis.AlongZ);
        BindRoofFormButton(gableFormButton, ERoofForm.Gable);
        BindRoofFormButton(hipFormButton, ERoofForm.Hip);
        BindRoofSlider(slopeSlider, value =>
        {
            UpdateSlopeLabel(value);
            onRoofSlopeChanged?.Invoke(value);
        });
        BindRoofSlider(brokenSlider, value =>
        {
            UpdateBrokenLabel(value);
            onRoofBrokenChanged?.Invoke(value);
        });
        ApplyToolVisibility();
        SetSelectionCount(0);
        BindOperationButton(paintButton, EEditorBrushOperation.Paint);
        BindOperationButton(eraseButton, EEditorBrushOperation.Erase);
        BindOperationButton(replaceButton, EEditorBrushOperation.Replace);
        BindShapeButton(voxelShapeButton, EEditorBrushShape.Voxel);
        BindShapeButton(floorShapeButton, EEditorBrushShape.Floor);
        BindShapeButton(wallShapeButton, EEditorBrushShape.Wall);
        BindShapeButton(fillShapeButton, EEditorBrushShape.Fill);
        BindShapeButton(roomShapeButton, EEditorBrushShape.Room);
        BindShapeButton(windowShapeButton, EEditorBrushShape.Window);
        BindShapeButton(doorShapeButton, EEditorBrushShape.Door);
        BindVoxelEdgesButton(autoEdgesButton, EEditorVoxelEdges.Auto);
        BindVoxelEdgesButton(blockyEdgesButton, EEditorVoxelEdges.Blocky);
        BindVoxelEdgesButton(steppedEdgesButton, EEditorVoxelEdges.Stepped);
        BindVoxelEdgesButton(smoothEdgesButton, EEditorVoxelEdges.Smooth);
        if (plateauSnapButton != null)
        {
            plateauSnapButton.Toggled += pressed => onPlateauSnapChanged?.Invoke(pressed);
        }
        if (snapToGridButton != null)
        {
            snapToGridButton.Toggled += pressed => onSnapToGridChanged?.Invoke(pressed);
        }
        if (snapRotationButton != null)
        {
            snapRotationButton.Toggled += pressed => onSnapRotationChanged?.Invoke(pressed);
        }
        if (lightingButton != null)
        {
            lightingButton.Toggled += pressed => onLightingChanged?.Invoke(pressed);
        }
        if (timeOfDaySlider != null)
        {
            timeOfDaySlider.ValueChanged += value => onTimeOfDayChanged?.Invoke((float)value);
        }
        if (weatherOption != null)
        {
            weatherOption.ItemSelected += index => onWeatherSelected?.Invoke((int)index);
        }
        if (interiorClassOption != null)
        {
            // Item 0 is Preserve, so the palette index is one less than the
            // item index — see BuildInteriorClassOptions.
            interiorClassOption.ItemSelected += index => onInteriorClassSelected?.Invoke((int)index - 1);
        }
    }

    private void BindOperationButton(Button button, EEditorBrushOperation operation)
    {
        if (button != null)
        {
            button.Pressed += () =>
            {
                _operation = operation;
                ApplyOperationButtons();
                onOperationSelected?.Invoke(operation);
            };
        }
    }

    private void BindShapeButton(Button button, EEditorBrushShape shape)
    {
        if (button != null)
        {
            button.Pressed += () => onShapeSelected?.Invoke(shape);
        }
    }

    private void BindVoxelEdgesButton(Button button, EEditorVoxelEdges edges)
    {
        if (button != null)
        {
            button.Pressed += () => onVoxelEdgesSelected?.Invoke(edges);
        }
    }

    // Snap is stored per brush shape by WorldEditor, which seats the toggle here
    // on every shape change. NoSignal, so restoring the stored value can't be
    // mistaken for the author toggling it and write straight back.
    public void SetPlateauSnap(bool snap, bool supported)
    {
        if (plateauSnapButton != null)
        {
            plateauSnapButton.SetPressedNoSignal(snap);
            plateauSnapButton.Disabled = !supported;
        }
    }

    // Seated by WorldEditor at startup from its own defaults. NoSignal, so
    // seating them can't be mistaken for the author toggling them.
    public void SetEntitySnaps(bool grid, bool rotation)
    {
        snapToGridButton?.SetPressedNoSignal(grid);
        snapRotationButton?.SetPressedNoSignal(rotation);
    }

    // Checked = the real game look; unchecked = the flat authoring view.
    public bool LightingChecked => lightingButton == null || lightingButton.ButtonPressed;

    // Pushed by WorldEditor at startup to seat the slider on its authored
    // default. NoSignal so seating it doesn't echo back as an author edit.
    public void SetTimeOfDay(float timeOfDay01)
    {
        timeOfDaySlider?.SetValueNoSignal(timeOfDay01);
    }

    public float TimeOfDayValue => timeOfDaySlider != null ? (float)timeOfDaySlider.Value : 0f;

    // Fills the weather dropdown and selects the first entry. Item indices
    // line up with `presets`, so the caller can index straight off the
    // selection. A preset's inspector "Resource Name" is its label; one that
    // never got a name falls back to its file name so the menu is never blank.
    public void BuildWeatherOptions(List<WeatherData> presets)
    {
        if (weatherOption == null)
        {
            return;
        }
        weatherOption.Clear();
        foreach (WeatherData preset in presets)
        {
            string label = string.IsNullOrEmpty(preset.ResourceName)
                ? preset.ResourcePath.GetFile().GetBaseName()
                : preset.ResourceName;
            weatherOption.AddItem(label);
        }
        if (weatherOption.ItemCount > 0)
        {
            weatherOption.Select(0);
        }
    }

    // Fills the interior-class dropdown from the world's space-class palette.
    // Item 0 is "Preserve" (palette index -1), so the remaining items line up
    // with SimData.interiorAmbiences at an offset of one — a scene that has
    // already been classified keeps its cells unless the author picks a class
    // explicitly, which is the safe default for re-saving an existing scene.
    public void BuildInteriorClassOptions(InteriorAmbienceData[] palette)
    {
        if (interiorClassOption == null)
        {
            return;
        }
        interiorClassOption.Clear();
        interiorClassOption.AddItem("Preserve");
        for (int i = 0; i < (palette?.Length ?? 0); i++)
        {
            InteriorAmbienceData data = palette[i];
            string label = data == null
                ? $"({i})"
                : string.IsNullOrEmpty(data.displayName)
                    ? data.ResourcePath.GetFile().GetBaseName()
                    : data.displayName;
            interiorClassOption.AddItem($"{i}: {label}");
        }
        interiorClassOption.Select(0);
    }

    // ----- Tool palette ----------------------------------------------------

    // Fills the voxel grid, the entity tabs and the roof grid with one toggle
    // button per brush. Each tool gets a ButtonGroup of its own so their
    // selections are independent, but the entity group spans all six tabs —
    // there is still only one entity brush selected at a time, so picking one in
    // Trees has to release whatever Rocks had pressed.
    public void BuildToolButtons(EditorBrushEntry[] voxels, EditorBrushEntry[] entities, EditorBrushEntry[] roofs)
    {
        _voxelEntries = voxels ?? Array.Empty<EditorBrushEntry>();
        _entityEntries = entities ?? Array.Empty<EditorBrushEntry>();
        _roofEntries = roofs ?? Array.Empty<EditorBrushEntry>();

        ClearGrid(voxelTab);
        _voxelButtons.Clear();
        var voxelGroup = new ButtonGroup();
        for (int i = 0; i < _voxelEntries.Length; i++)
        {
            _voxelButtons.Add(AddBrushButton(voxelTab, _voxelEntries[i], voxelGroup, i, index => onVoxelBrushSelected?.Invoke(index)));
        }

        foreach (EEditorEntityTab tab in Enum.GetValues<EEditorEntityTab>())
        {
            ClearGrid(ContainerForTab(tab));
        }
        _entityButtons.Clear();
        var entityGroup = new ButtonGroup();
        for (int i = 0; i < _entityEntries.Length; i++)
        {
            Container grid = ContainerForTab(_entityEntries[i].Tab);
            _entityButtons.Add(AddBrushButton(grid, _entityEntries[i], entityGroup, i, index => onEntityBrushSelected?.Invoke(index)));
        }

        ClearGrid(roofTab);
        _roofButtons.Clear();
        var roofGroup = new ButtonGroup();
        for (int i = 0; i < _roofEntries.Length; i++)
        {
            _roofButtons.Add(AddBrushButton(roofTab, _roofEntries[i], roofGroup, i, index => onRoofBrushSelected?.Invoke(index)));
        }
    }

    // Icons that arrive after the buttons are built — the icon baker renders
    // one brush per frame, so a palette opens on name labels and fills in.
    public void SetEntityIcon(int index, Texture2D icon)
    {
        if (index < 0 || index >= _entityEntries.Length || icon == null)
        {
            return;
        }
        _entityEntries[index] = new EditorBrushEntry(_entityEntries[index].Name, icon, _entityEntries[index].Tab);
        _entityButtons[index]?.Bind(_entityEntries[index]);
    }

    private Container ContainerForTab(EEditorEntityTab tab)
    {
        return tab switch
        {
            EEditorEntityTab.Interactives => interactivesTab,
            EEditorEntityTab.Trees => treesTab,
            EEditorEntityTab.Rocks => rocksTab,
            EEditorEntityTab.Nature => natureTab,
            EEditorEntityTab.Furniture => furnitureTab,
            EEditorEntityTab.Props => propsTab,
            _ => null,
        };
    }

    private static void ClearGrid(Container grid)
    {
        if (grid == null)
        {
            return;
        }
        foreach (Node child in grid.GetChildren())
        {
            child.QueueFree();
        }
    }

    // Null when there's no grid to put the button in — the caller still records
    // the slot so brush indices stay aligned with the palette.
    private EditorToolButton AddBrushButton(Container grid, EditorBrushEntry entry, ButtonGroup group, int index, Action<int> onSelected)
    {
        if (grid == null || toolButtonScene == null)
        {
            return null;
        }
        var button = toolButtonScene.Instantiate<EditorToolButton>();
        button.ToggleMode = true;
        button.ButtonGroup = group;
        button.Bind(entry);
        button.Pressed += () => onSelected(index);
        grid.AddChild(button);
        return button;
    }

    // ----- Selection state -------------------------------------------------

    // Pushed by WorldEditor whenever the brush changes from ANY source (button
    // click, Q/E cycling, startup) — SetPressedNoSignal keeps a click from
    // echoing back out as another selection callback.
    public void SetVoxelBrush(int index)
    {
        SelectButton(_voxelButtons, index);
        UpdateBrushSummary(_voxelEntries, index);
    }

    // No summary to update: it lives in the voxel-only panel, and an entity
    // button carries its own name label and tooltip.
    public void SetEntityBrush(int index)
    {
        SelectButton(_entityButtons, index);
    }

    public void SetRoofBrush(int index)
    {
        SelectButton(_roofButtons, index);
    }

    private static void SelectButton(List<EditorToolButton> buttons, int index)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            buttons[i]?.SetPressedNoSignal(i == index);
        }
    }

    private void UpdateBrushSummary(EditorBrushEntry[] entries, int index)
    {
        if (index < 0 || index >= entries.Length)
        {
            return;
        }
        if (brushImage != null)
        {
            brushImage.Texture = entries[index].Icon;
        }
        if (brushNameLabel != null)
        {
            brushNameLabel.Text = entries[index].Name;
        }
    }

    public EEditorTool Tool => _tool;

    private void BindToolButton(Button button, EEditorTool tool)
    {
        if (button != null)
        {
            button.Pressed += () => SelectTool(tool);
        }
    }

    // The Toolbox buttons pick what a click paints, and with it which palette
    // the bottom bar shows. Re-pressing the button that's already down re-emits
    // Pressed (a grouped toggle can't be released by clicking it), so an
    // unchanged tool must not fan out as a selection change.
    private void SelectTool(EEditorTool tool)
    {
        if (tool == _tool)
        {
            return;
        }
        _tool = tool;
        ApplyToolVisibility();
        onToolChanged?.Invoke(tool);
    }

    private void ApplyToolVisibility()
    {
        voxelToolButton?.SetPressedNoSignal(_tool == EEditorTool.Voxel);
        entityToolButton?.SetPressedNoSignal(_tool == EEditorTool.Entity);
        roofToolButton?.SetPressedNoSignal(_tool == EEditorTool.Roof);
        SetVisible(voxelPalette, _tool == EEditorTool.Voxel);
        SetVisible(voxelInfoPanel, _tool == EEditorTool.Voxel);
        SetVisible(entityPalette, _tool == EEditorTool.Entity);
        SetVisible(entityDetailsPanel, _tool == EEditorTool.Entity);
        SetVisible(roofPalette, _tool == EEditorTool.Roof);
        SetVisible(roofInfoPanel, _tool == EEditorTool.Roof);
    }

    private static void SetVisible(Control control, bool visible)
    {
        if (control != null)
        {
            control.Visible = visible;
        }
    }

    // ----- Roof options ----------------------------------------------------

    // Same re-press guard as every other grouped toggle here: clicking the one
    // already down re-emits Pressed, and re-announcing it would be noise.
    private EEditorRoofMode _roofMode = EEditorRoofMode.Draw;

    private void BindRoofModeButton(Button button, EEditorRoofMode mode)
    {
        if (button != null)
        {
            button.Pressed += () =>
            {
                if (mode == _roofMode)
                {
                    return;
                }
                _roofMode = mode;
                onRoofModeChanged?.Invoke(mode);
            };
        }
    }

    private void BindSeamAxisButton(Button button, ERoofSeamAxis axis)
    {
        if (button != null)
        {
            button.Pressed += () =>
            {
                onRoofSeamAxisChanged?.Invoke(axis);
                onRoofSettingsCommitted?.Invoke();
            };
        }
    }

    private void BindRoofFormButton(Button button, ERoofForm form)
    {
        if (button != null)
        {
            button.Pressed += () =>
            {
                onRoofFormChanged?.Invoke(form);
                onRoofSettingsCommitted?.Invoke();
            };
        }
    }

    // True while a roof slider is being dragged, which is what holds the push
    // back until release. Keyboard / wheel nudges never enter a drag, so they
    // fall through to the immediate push in ValueChanged.
    private bool _roofSliderDragging;

    private void BindRoofSlider(HSlider slider, Action<float> onValue)
    {
        if (slider == null)
        {
            return;
        }
        slider.DragStarted += () => _roofSliderDragging = true;
        slider.DragEnded += changed =>
        {
            _roofSliderDragging = false;
            if (changed)
            {
                onRoofSettingsCommitted?.Invoke();
            }
        };
        slider.ValueChanged += value =>
        {
            onValue((float)value);
            if (!_roofSliderDragging)
            {
                onRoofSettingsCommitted?.Invoke();
            }
        };
    }

    // Pushed by WorldEditor at startup to seat the controls on their authored
    // defaults. NoSignal so seating them doesn't echo back as an author edit.
    public void SetRoofSeamAxis(ERoofSeamAxis axis)
    {
        ridgeXButton?.SetPressedNoSignal(axis == ERoofSeamAxis.AlongX);
        ridgeZButton?.SetPressedNoSignal(axis == ERoofSeamAxis.AlongZ);
    }

    public void SetRoofForm(ERoofForm form)
    {
        gableFormButton?.SetPressedNoSignal(form == ERoofForm.Gable);
        hipFormButton?.SetPressedNoSignal(form == ERoofForm.Hip);
    }

    public void SetRoofSlope(float degrees)
    {
        slopeSlider?.SetValueNoSignal(degrees);
        UpdateSlopeLabel(degrees);
    }

    private void UpdateSlopeLabel(float degrees)
    {
        if (slopeLabel != null)
        {
            slopeLabel.Text = $"Slope: {degrees:F0}°";
        }
    }

    public float RoofSlopeValue => slopeSlider != null ? (float)slopeSlider.Value : 0f;

    public void SetRoofBroken(float broken)
    {
        brokenSlider?.SetValueNoSignal(broken);
        UpdateBrokenLabel(broken);
    }

    private void UpdateBrokenLabel(float broken)
    {
        if (brokenLabel != null)
        {
            brokenLabel.Text = broken <= 0f ? "Broken: none" : $"Broken: {broken * 100f:F0}%";
        }
    }

    private void BindEntityModeButton(Button button, EEditorEntityMode mode)
    {
        if (button != null)
        {
            button.Pressed += () => SelectEntityToolMode(mode);
        }
    }

    // Place vs Select. Same re-press guard as the tool row: a grouped toggle
    // re-emits Pressed when you click the one already down, and re-announcing an
    // unchanged mode would drop the selection out from under the author.
    private void SelectEntityToolMode(EEditorEntityMode mode)
    {
        if (mode == _entityToolMode)
        {
            return;
        }
        _entityToolMode = mode;
        ApplyEntityToolMode();
        onEntityToolModeChanged?.Invoke(mode);
    }

    private void ApplyEntityToolMode()
    {
        placeModeButton?.SetPressedNoSignal(_entityToolMode == EEditorEntityMode.Place);
        selectModeButton?.SetPressedNoSignal(_entityToolMode == EEditorEntityMode.Select);
    }

    public EEditorEntityMode EntityToolMode => _entityToolMode;

    // Pushed every frame by WorldEditor while Select mode is up, so the readout
    // tracks a selection that shrank on its own (an undo, a chunk eviction).
    public void SetSelectionCount(int count)
    {
        if (selectionLabel == null)
        {
            return;
        }
        selectionLabel.Text = count switch
        {
            0 => "No selection",
            1 => "1 selected",
            _ => $"{count} selected",
        };
    }

    // ----- Paint / erase ---------------------------------------------------

    // Ctrl / Alt are momentary overrides: they show their operation selected
    // while held, then restore the panel-chosen one. Polled per frame rather
    // than driven off key events so a release outside the window can't strand
    // the display on the override.
    public void SetHeldOverride(EEditorBrushOperation? held)
    {
        if (held == _heldOverride)
        {
            return;
        }
        _heldOverride = held;
        ApplyOperationButtons();
    }

    private void ApplyOperationButtons()
    {
        EEditorBrushOperation active = _heldOverride ?? _operation;
        paintButton?.SetPressedNoSignal(active == EEditorBrushOperation.Paint);
        eraseButton?.SetPressedNoSignal(active == EEditorBrushOperation.Erase);
        replaceButton?.SetPressedNoSignal(active == EEditorBrushOperation.Replace);
    }

    public void SetShape(EEditorBrushShape shape)
    {
        ButtonForShape(shape)?.SetPressedNoSignal(true);
    }

    private Button ButtonForShape(EEditorBrushShape shape)
    {
        return shape switch
        {
            EEditorBrushShape.Voxel => voxelShapeButton,
            EEditorBrushShape.Floor => floorShapeButton,
            EEditorBrushShape.Wall => wallShapeButton,
            EEditorBrushShape.Fill => fillShapeButton,
            EEditorBrushShape.Room => roomShapeButton,
            EEditorBrushShape.Window => windowShapeButton,
            EEditorBrushShape.Door => doorShapeButton,
            _ => null,
        };
    }

    // ----- Readouts --------------------------------------------------------

    // buildY is where a click into empty air lands — worth showing alongside the
    // cutaway, since with the clip off the two no longer imply each other.
    public void UpdateClip(float clipY, bool clipOff, int buildY)
    {
        if (clipLabel != null)
        {
            string clip = clipOff ? "None" : $"Y={clipY:F0}";
            clipLabel.Text = $"Clip: {clip} | Build: Y={buildY}";
        }
    }

    // True while the pointer sits on a HUD control, so the world tools can drop
    // their previews rather than picking whatever a panel happens to cover.
    // Layout-only wrappers (the margin / box containers that merely position the
    // panels) are authored MouseFilter=Ignore, so their large transparent rects
    // don't read as HUD — anything counted here is a control that draws.
    public bool IsPointerOverUi()
    {
        return GetViewport().GuiGetHoveredControl() != null;
    }

    // The voxel cell under the mouse, or null when the pick resolves to nothing.
    public void UpdatePosition(Vector3I? cell)
    {
        if (coordsLabel != null)
        {
            coordsLabel.Text = cell.HasValue
                ? $"Pos: ({cell.Value.X}, {cell.Value.Y}, {cell.Value.Z})"
                : "Pos: --";
        }
    }

    // The open document — kind, where it will be written, and whether that file
    // exists yet (a new document carries a proposed path nothing has written).
    public void SetDocument(string kindLabel, string path, bool saved)
    {
        if (documentLabel == null)
        {
            return;
        }
        if (string.IsNullOrEmpty(path))
        {
            documentLabel.Text = $"{kindLabel}: (unsaved)";
            return;
        }
        documentLabel.Text = saved ? $"{kindLabel}: {path}" : $"{kindLabel}: {path} (unsaved)";
    }

    public void ShowToast(string message, bool success)
    {
        if (toastLabel == null)
        {
            return;
        }
        toastLabel.Text = message;
        toastLabel.Modulate = success ? toastSuccessColor : toastFailureColor;
        toastLabel.Visible = true;
        _toastRemaining = toastSeconds;
    }

    // Wall clock, not the sim clock: the toast is pure presentation, and the
    // editor never ticks the sim anyway.
    public override void _Process(double delta)
    {
        if (_toastRemaining <= 0f)
        {
            return;
        }
        _toastRemaining -= (float)delta;
        if (_toastRemaining <= 0f && toastLabel != null)
        {
            toastLabel.Visible = false;
        }
    }
}
