using System;
using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class EditorHud : CanvasLayer
{
    [Export] public Label clipLabel;
    [Export] public Label coordsLabel;
    [Export] public Label helpLabel;

    [ExportGroup("Tool Palette")]
    [Export] public PackedScene toolButtonScene;
    [Export] public TabContainer toolTabs;
    [Export] public GridContainer voxelTab;
    [Export] public GridContainer entityTab;

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
    [Export] public Button windowShapeButton;
    [Export] public Button doorShapeButton;
    [Export] public Button plateauSnapButton;

    public Action<int> onVoxelBrushSelected;
    public Action<int> onEntityBrushSelected;
    public Action<bool> onEntityModeChanged;
    public Action<EEditorBrushOperation> onOperationSelected;
    public Action<EEditorBrushShape> onShapeSelected;
    public Action<bool> onPlateauSnapChanged;

    // Tab order must match the two GridContainers wired above.
    private const int VOXEL_TAB = 0;
    private const int ENTITY_TAB = 1;

    private readonly List<EditorToolButton> _voxelButtons = new List<EditorToolButton>();
    private readonly List<EditorToolButton> _entityButtons = new List<EditorToolButton>();
    private EditorBrushEntry[] _voxelEntries = Array.Empty<EditorBrushEntry>();
    private EditorBrushEntry[] _entityEntries = Array.Empty<EditorBrushEntry>();

    // The persistent operation chosen on the panel. A held Ctrl / Alt shows its
    // operation pressed without touching this, so releasing the modifier snaps
    // the row back to what was chosen.
    private EEditorBrushOperation _operation = EEditorBrushOperation.Paint;
    private EEditorBrushOperation? _heldOverride;

    public override void _Ready()
    {
        if (helpLabel != null)
        {
            helpLabel.Text = "LMB: Paint | Ctrl+LMB: Erase | Alt+LMB: Replace | R/F: Up/Down | Z/C: Rotate | Ctrl+S: Save | Esc: Quit";
        }

        if (toolTabs != null)
        {
            toolTabs.TabChanged += OnTabChanged;
        }
        BindOperationButton(paintButton, EEditorBrushOperation.Paint);
        BindOperationButton(eraseButton, EEditorBrushOperation.Erase);
        BindOperationButton(replaceButton, EEditorBrushOperation.Replace);
        BindShapeButton(voxelShapeButton, EEditorBrushShape.Voxel);
        BindShapeButton(floorShapeButton, EEditorBrushShape.Floor);
        BindShapeButton(wallShapeButton, EEditorBrushShape.Wall);
        BindShapeButton(fillShapeButton, EEditorBrushShape.Fill);
        BindShapeButton(windowShapeButton, EEditorBrushShape.Window);
        BindShapeButton(doorShapeButton, EEditorBrushShape.Door);
        if (plateauSnapButton != null)
        {
            plateauSnapButton.Toggled += pressed => onPlateauSnapChanged?.Invoke(pressed);
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
            button.Pressed += () =>
            {
                ApplyPlateauSnapAvailability(shape);
                onShapeSelected?.Invoke(shape);
            };
        }
    }

    // The toggle keeps its own state across shape changes — greying it out for
    // a shape that can't snap must not silently clear the author's choice for
    // the ones that can, so only Disabled moves here.
    private void ApplyPlateauSnapAvailability(EEditorBrushShape shape)
    {
        if (plateauSnapButton != null)
        {
            plateauSnapButton.Disabled = !WorldEditor.SupportsPlateauSnap(shape);
        }
    }

    // The raw toggle state, independent of whether the current shape can use it
    // — the caller applies that gate. Folding Disabled in here would read as
    // "off" for the startup shape and stick, since Toggled only fires on input.
    public bool PlateauSnapChecked => plateauSnapButton != null && plateauSnapButton.ButtonPressed;

    // ----- Tool palette ----------------------------------------------------

    // Fills the Voxels / Entities tabs with one toggle button per brush. Each
    // tab gets its own ButtonGroup so the two selections are independent.
    public void BuildToolButtons(EditorBrushEntry[] voxels, EditorBrushEntry[] entities)
    {
        _voxelEntries = voxels ?? Array.Empty<EditorBrushEntry>();
        _entityEntries = entities ?? Array.Empty<EditorBrushEntry>();
        FillTab(voxelTab, _voxelEntries, _voxelButtons, i => onVoxelBrushSelected?.Invoke(i));
        FillTab(entityTab, _entityEntries, _entityButtons, i => onEntityBrushSelected?.Invoke(i));
    }

    private void FillTab(GridContainer tab, EditorBrushEntry[] entries, List<EditorToolButton> buttons, Action<int> onSelected)
    {
        buttons.Clear();
        if (tab == null || toolButtonScene == null)
        {
            return;
        }
        foreach (Node child in tab.GetChildren())
        {
            child.QueueFree();
        }

        var group = new ButtonGroup();
        for (int i = 0; i < entries.Length; i++)
        {
            var button = toolButtonScene.Instantiate<EditorToolButton>();
            button.ToggleMode = true;
            button.ButtonGroup = group;
            button.Bind(entries[i]);
            int index = i;
            button.Pressed += () => onSelected(index);
            tab.AddChild(button);
            buttons.Add(button);
        }
    }

    // ----- Selection state -------------------------------------------------

    // Pushed by WorldEditor whenever the brush changes from ANY source (button
    // click, Q/E cycling, startup) — SetPressedNoSignal keeps a click from
    // echoing back out as another selection callback.
    public void SetVoxelBrush(int index)
    {
        SelectButton(_voxelButtons, index);
        if (!IsEntityMode)
        {
            UpdateBrushSummary(_voxelEntries, index);
        }
    }

    public void SetEntityBrush(int index)
    {
        SelectButton(_entityButtons, index);
        if (IsEntityMode)
        {
            UpdateBrushSummary(_entityEntries, index);
        }
    }

    private static void SelectButton(List<EditorToolButton> buttons, int index)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            buttons[i].SetPressedNoSignal(i == index);
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

    // Voxel vs entity mode is purely which palette tab is open — there is no
    // separate mode toggle.
    public bool IsEntityMode => toolTabs != null && toolTabs.CurrentTab == ENTITY_TAB;

    private void OnTabChanged(long tab)
    {
        onEntityModeChanged?.Invoke(tab == ENTITY_TAB);
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
        ApplyPlateauSnapAvailability(shape);
    }

    private Button ButtonForShape(EEditorBrushShape shape)
    {
        return shape switch
        {
            EEditorBrushShape.Voxel => voxelShapeButton,
            EEditorBrushShape.Floor => floorShapeButton,
            EEditorBrushShape.Wall => wallShapeButton,
            EEditorBrushShape.Fill => fillShapeButton,
            EEditorBrushShape.Window => windowShapeButton,
            EEditorBrushShape.Door => doorShapeButton,
            _ => null,
        };
    }

    // ----- Readouts --------------------------------------------------------

    public void UpdateClip(float clipY)
    {
        if (clipLabel != null)
        {
            if (clipY >= float.PositiveInfinity)
            {
                clipLabel.Text = "Clip: None";
            }
            else
            {
                clipLabel.Text = $"Clip: Y={clipY:F0}";
            }
        }
    }

    public void UpdatePosition(Vector3 pos)
    {
        if (coordsLabel != null)
        {
            coordsLabel.Text = $"Pos: ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})";
        }
    }
}
