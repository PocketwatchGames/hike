using Godot;
using System.Collections.Generic;

// Property editor for the entity tool's selected placement, top-right of the
// painter.
//
// A hand-placed entity's properties ARE its SpawnEntryData's — the text on a
// signpost, the conditions on a chest — so this REFLECTS the entry rather than
// naming fields: an entry type written tomorrow is editable the day it is
// written, and there is no parallel list of overrides to keep in step with the
// spawn entries.
//
// Scalars only (string, number, bool, enum, flags). Anything resource-shaped —
// a loot table, a mob descriptor, a language — shows as a read-only row: varying
// it per placement would need a resource picker, and picking a ready-made
// variant off the palette is the faster authoring move anyway. The row is there
// rather than hidden so the panel never implies the entry holds less than it
// does.
[GlobalClass]
public partial class WorldMapEntityInspector : PanelContainer
{
    [Export] public Label titleLabel;
    [Export] public Container rows;
    // Width of the name column, so the editors line up down the panel.
    [Export] public int labelWidth = 130;
    // Height of the box a multiline string gets.
    [Export] public int multilineHeight = 72;
    [Export] public Color readOnlyColor = new Color(0.75f, 0.75f, 0.8f, 0.6f);

    // Bracket one property change as one undo step. The painter owns the
    // history; this owns the widgets.
    public System.Action BeforeEdit;
    public System.Action AfterEdit;

    // What the rows were built for. Rebuilding every frame would destroy the
    // widget being typed into, so the panel rebuilds only when the selection —
    // or the entry under it, which the first edit forks — actually changes.
    private EntityPlacement _shown;
    private SpawnEntryData _shownEntry;
    // The placement the LIVE WIDGETS edit, which is not always the one being
    // shown: a row can fire its commit while the panel is already switching to
    // another entity (releasing focus destroys the widget). Reading and writing
    // through this instead of through _shown is what stops one signpost's text
    // landing on the next one selected.
    private EntityPlacement _rowsOwner;
    private readonly List<System.Action> _refreshers = new();
    // Is a run of typing holding an undo step open? Text applies per keystroke,
    // so the step is opened by the first one and closed when the field is left —
    // otherwise a typed sentence would be a dozen entries on the undo stack.
    private bool _typing;

    // Commit whatever is half-typed. A row commits on Enter or on losing focus,
    // and nothing in the painter takes focus away — the map canvas is
    // FOCUS_NONE, so clicking the map (or a HUD button) leaves the text box
    // focused and its typed value uncommitted, which is how an edit was lost
    // between selecting one entity and the next. Releasing focus runs each
    // widget's OWN commit path (the text rows' FocusExited, a SpinBox's internal
    // apply), so this needs no per-row registry.
    public void FlushPendingEdit()
    {
        Control focused = GetViewport()?.GuiGetFocusOwner();
        if (focused != null && IsAncestorOf(focused))
        {
            focused.ReleaseFocus();
        }
        // The release above ends a typing run through the field's own
        // FocusExited; this catches a run whose widget is already gone.
        EndTyping();
    }

    // Close the undo step a run of typing opened. The text is already IN the
    // entry — this only decides where one undo lands.
    private void EndTyping()
    {
        if (!_typing)
        {
            return;
        }
        _typing = false;
        AfterEdit?.Invoke();
    }

    // The selected placement, or null for "nothing selected". Called every
    // frame.
    public void Show(EntityPlacement placement)
    {
        SpawnEntryData entry = placement?.entry;
        if (entry == null)
        {
            if (_shown != null)
            {
                // Before the widgets go, and while _rowsOwner still names who
                // they belong to.
                FlushPendingEdit();
                _shown = null;
                _shownEntry = null;
                Clear();
            }
            Visible = false;
            return;
        }
        Visible = true;
        if (ReferenceEquals(placement, _shown) && ReferenceEquals(entry, _shownEntry))
        {
            // Same rows, possibly different values — an undo changes what the
            // entry holds without touching which entry it is.
            if (titleLabel != null)
            {
                titleLabel.Text = SpawnEntryData.DisplayName(entry);
            }
            RefreshRows();
            return;
        }
        FlushPendingEdit();
        // Assigned BEFORE the rebuild: a commit the flush triggers calls back
        // through AfterEdit into Show, and these are what make that call take the
        // same-rows path instead of re-entering Rebuild mid-clear.
        _shown = placement;
        _shownEntry = entry;
        Rebuild();
    }

    private void Clear()
    {
        _refreshers.Clear();
        _rowsOwner = null;
        if (rows == null)
        {
            return;
        }
        foreach (Node child in rows.GetChildren())
        {
            // Detached now rather than at the free, or the old rows share the
            // panel with the new ones for a frame.
            rows.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void Rebuild()
    {
        Clear();
        if (rows == null || _shownEntry == null)
        {
            return;
        }
        _rowsOwner = _shown;
        if (titleLabel != null)
        {
            titleLabel.Text = SpawnEntryData.DisplayName(_shownEntry);
        }
        foreach (Godot.Collections.Dictionary property in _shownEntry.GetPropertyList())
        {
            // ScriptVariable is the flag Godot sets on a script's own exports, so
            // engine bookkeeping (resource_path, script) never reaches the panel.
            var usage = (PropertyUsageFlags)(long)property["usage"];
            if ((usage & PropertyUsageFlags.ScriptVariable) == 0)
            {
                continue;
            }
            var name = new StringName(property["name"].AsString());
            if (!SpawnEntryData.IsHandPlacedProperty(name))
            {
                continue;
            }
            AddRow(name, (Variant.Type)(long)property["type"],
                (PropertyHint)(long)property["hint"], property["hint_string"].AsString());
        }
        // A row's initial state comes from the SAME refresher that keeps it up
        // to date, so a row type cannot be built with one and forget the other —
        // which is what left every flags row reading as all-unchecked whatever
        // the entry held, and made a reselect look like the edit had reverted.
        RefreshRows();
    }

    private void RefreshRows()
    {
        foreach (System.Action refresh in _refreshers)
        {
            refresh();
        }
    }

    private void AddRow(StringName name, Variant.Type type, PropertyHint hint, string hintString)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label
        {
            Text = name.ToString(),
            CustomMinimumSize = new Vector2(labelWidth, 0f),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Control editor = BuildEditor(name, type, hint, hintString);
        editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(editor);
        rows.AddChild(row);
    }

    private Control BuildEditor(StringName name, Variant.Type type, PropertyHint hint, string hintString)
    {
        switch (type)
        {
            case Variant.Type.String or Variant.Type.StringName:
                return hint == PropertyHint.MultilineText
                    ? BuildMultiline(name)
                    : BuildLine(name);
            case Variant.Type.Bool:
                return BuildCheck(name);
            case Variant.Type.Int when hint == PropertyHint.Enum:
                return BuildEnum(name, hintString);
            case Variant.Type.Int when hint == PropertyHint.Flags:
                return BuildFlags(name, hintString);
            case Variant.Type.Int or Variant.Type.Float:
                return BuildNumber(name, type == Variant.Type.Int, hint, hintString);
            default:
                return BuildReadOnly(name);
        }
    }

    // Reading and writing always go through the placement, never through a
    // captured entry reference: the first edit REPLACES the entry with a fork and
    // every row must follow it there.
    private Variant Read(StringName name)
    {
        return _rowsOwner?.entry != null ? _rowsOwner.entry.Get(name) : default;
    }

    // Text applies as it is TYPED, not on Enter or on leaving the field. The
    // multiline rows are the reason it cannot be Enter: a signpost's text is
    // several lines, so Enter is a newline there and never a commit — which left
    // clicking away as the only way to save one, and clicking away is exactly
    // what an author does without thinking about it.
    //
    // The undo step is what the old commit-on-leave was really protecting, and it
    // is kept by BRACKETING instead: the first keystroke opens one step and
    // leaving the field closes it, so a typed sentence is still one undo.
    private void ApplyLive(StringName name, Variant value)
    {
        SpawnEntryData current = _rowsOwner?.entry;
        if (current == null || current.Get(name).ToString() == value.ToString())
        {
            return;
        }
        if (!_typing)
        {
            // Snapshots the BEFORE state, so it has to happen ahead of the first
            // character reaching the entry.
            BeforeEdit?.Invoke();
            _typing = true;
        }
        SpawnEntryData target = _rowsOwner.EditableEntry();
        if (ReferenceEquals(_rowsOwner, _shown))
        {
            _shownEntry = target;
        }
        target.Set(name, value);
    }

    private void Commit(StringName name, Variant value)
    {
        SpawnEntryData current = _rowsOwner?.entry;
        if (current == null)
        {
            return;
        }
        // A row commits on losing focus as well as on Enter, so clicking into a
        // box and back out reaches here with the value it already had. Forking
        // the palette entry for that would silently stop the placement tracking
        // the palette, and it would cost an undo slot. Compared as TEXT because
        // Variant does not compare by value here — the undo aspect pays for the
        // same thing.
        if (current.Get(name).ToString() == value.ToString())
        {
            return;
        }
        BeforeEdit?.Invoke();
        SpawnEntryData target = _rowsOwner.EditableEntry();
        if (ReferenceEquals(_rowsOwner, _shown))
        {
            _shownEntry = target;
        }
        target.Set(name, value);
        AfterEdit?.Invoke();
    }

    private Control BuildLine(StringName name)
    {
        var edit = new LineEdit();
        edit.TextChanged += _ => ApplyLive(name, edit.Text);
        // Enter ENDS the undo step rather than committing the value — the value
        // is already in. Leaving the field applies once more first, since a paste
        // or an undo inside the box can move the text without a keystroke.
        edit.TextSubmitted += _ => EndTyping();
        edit.FocusExited += () =>
        {
            ApplyLive(name, edit.Text);
            EndTyping();
        };
        _refreshers.Add(() =>
        {
            if (!edit.HasFocus())
            {
                edit.Text = Read(name).AsString();
            }
        });
        return edit;
    }

    private Control BuildMultiline(StringName name)
    {
        var edit = new TextEdit
        {
            CustomMinimumSize = new Vector2(0f, multilineHeight),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
        };
        edit.TextChanged += () => ApplyLive(name, edit.Text);
        edit.FocusExited += () =>
        {
            ApplyLive(name, edit.Text);
            EndTyping();
        };
        _refreshers.Add(() =>
        {
            if (!edit.HasFocus())
            {
                edit.Text = Read(name).AsString();
            }
        });
        return edit;
    }

    private Control BuildCheck(StringName name)
    {
        var check = new CheckBox();
        check.Toggled += on => Commit(name, on);
        _refreshers.Add(() => check.SetPressedNoSignal(Read(name).AsBool()));
        return check;
    }

    private Control BuildNumber(StringName name, bool integer, PropertyHint hint, string hintString)
    {
        var spin = new SpinBox
        {
            // Wide open unless the property authored a range — a SpinBox's own
            // 0..100 default would silently clamp a radius or a count.
            MinValue = -1e9,
            MaxValue = 1e9,
            Step = integer ? 1d : 0.001d,
        };
        if (hint == PropertyHint.Range)
        {
            string[] parts = hintString.Split(',');
            if (parts.Length >= 2 && float.TryParse(parts[0], out float min) && float.TryParse(parts[1], out float max))
            {
                spin.MinValue = min;
                spin.MaxValue = max;
                // or_greater / or_less on the end of the hint mean the range is a
                // suggestion, not a wall.
                spin.AllowGreater = hintString.Contains("or_greater");
                spin.AllowLesser = hintString.Contains("or_less");
            }
            if (parts.Length >= 3 && float.TryParse(parts[2], out float step) && step > 0f)
            {
                spin.Step = step;
            }
        }
        spin.ValueChanged += v => Commit(name, integer ? Variant.From((int)v) : Variant.From((float)v));
        _refreshers.Add(() =>
        {
            if (!spin.GetLineEdit().HasFocus())
            {
                spin.SetValueNoSignal(integer ? Read(name).AsInt32() : Read(name).AsSingle());
            }
        });
        return spin;
    }

    private Control BuildEnum(StringName name, string hintString)
    {
        var option = new OptionButton();
        foreach ((string label, int value) in ParseHintItems(hintString, flags: false))
        {
            option.AddItem(label, value);
        }
        option.ItemSelected += index => Commit(name, option.GetItemId((int)index));
        _refreshers.Add(() =>
        {
            int current = Read(name).AsInt32();
            for (int i = 0; i < option.ItemCount; i++)
            {
                if (option.GetItemId(i) == current)
                {
                    option.Selected = i;
                    return;
                }
            }
            option.Selected = -1;
        });
        return option;
    }

    // One checkbox per bit rather than a dropdown, because the value is a SET —
    // "day AND clear" is a normal thing to want from a chest.
    private Control BuildFlags(StringName name, string hintString)
    {
        var box = new HFlowContainer();
        var boxes = new List<(CheckBox Check, int Bit)>();
        foreach ((string label, int value) in ParseHintItems(hintString, flags: true))
        {
            // A zero-valued member (the conventional `None = 0`) names the EMPTY
            // set, not a bit: `mask | 0` and `mask & ~0` are both the mask, so
            // its box could never write anything and `(mask & 0) == 0` drew it
            // permanently ticked. Clearing every other box already means None.
            if (value == 0)
            {
                continue;
            }
            var check = new CheckBox { Text = label };
            int bit = value;
            check.Toggled += on =>
            {
                int mask = Read(name).AsInt32();
                Commit(name, on ? mask | bit : mask & ~bit);
            };
            box.AddChild(check);
            boxes.Add((check, bit));
        }
        _refreshers.Add(() =>
        {
            int mask = Read(name).AsInt32();
            foreach ((CheckBox check, int bit) in boxes)
            {
                check.SetPressedNoSignal((mask & bit) == bit);
            }
        });
        return box;
    }

    // An enum/flags hint is "Name,Other" or "Name:4,Other:8". Godot spells the
    // values out for the exports reached here, so the implicit form is a
    // fallback — and it differs by kind: an enum's nth item is n, a flag's is
    // the nth bit.
    private static List<(string Label, int Value)> ParseHintItems(string hintString, bool flags)
    {
        var items = new List<(string, int)>();
        if (string.IsNullOrEmpty(hintString))
        {
            return items;
        }
        string[] parts = hintString.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            int colon = part.LastIndexOf(':');
            if (colon >= 0 && int.TryParse(part[(colon + 1)..], out int value))
            {
                items.Add((part[..colon], value));
            }
            else
            {
                items.Add((part, flags ? 1 << i : i));
            }
        }
        return items;
    }

    private Control BuildReadOnly(StringName name)
    {
        var label = new Label { VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeColorOverride("font_color", readOnlyColor);
        _refreshers.Add(() => label.Text = Summarize(Read(name)));
        return label;
    }

    // Enough to recognise what is in a field the panel cannot edit.
    private static string Summarize(Variant value)
    {
        switch (value.VariantType)
        {
            case Variant.Type.Nil:
                return "—";
            case Variant.Type.Object:
                var resource = value.As<Resource>();
                if (resource == null)
                {
                    return "—";
                }
                return !string.IsNullOrEmpty(resource.ResourcePath)
                    ? resource.ResourcePath.GetFile().GetBaseName()
                    : resource.GetType().Name;
            case Variant.Type.Array:
                return $"{value.AsGodotArray().Count} item(s)";
            default:
                return value.ToString();
        }
    }
}
