using System;
using Godot;
using System.Collections.Generic;
using System.Linq;

// What the active tool has SELECTED, top-right of the painter. Two modes,
// because two tools have a selection worth reading:
//
//   a hand-placed ENTITY  — its properties, editable (the entity tool);
//   a scatter SET         — what spawns from it and how much, read-only
//                           (the mob tool).
//
// A hand-placed entity's properties ARE its SpawnEntryData's — the text on a
// signpost, the conditions on a chest — so this REFLECTS the entry rather than
// naming fields: an entry type written tomorrow is editable the day it is
// written, and there is no parallel list of overrides to keep in step with the
// spawn entries.
//
// Scalars (string, number, bool, enum, flags) get an editor, and so does a
// SINGLE resource-typed field — a conversation, a language, a mob descriptor, a
// recruit template — through a dropdown filled by ResourceTypeIndex. That one
// matters most for NPCs, where every placement is genuinely its own individual
// with its own dialogue rather than a copy of a species template, so authoring a
// palette file per villager is the wrong shape.
//
// A LIST of resources — a chest's loot, a merchant's stock, an NPC's gifts — is
// edited too: one block per element with the element's own fields, plus add and
// remove. That is what lets one `chest` palette row stand for every chest, with
// what it holds picked per placement.
//
// What stays a read-only row: a list of strings or scenes (an outfit, a stone
// ring's scenes) and a PackedScene, which is a rig choice rather than data. The
// row is there rather than hidden so the panel never implies the entry holds
// less than it does.
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
    // How far a list's elements sit in from its name, and each element's fields
    // in from its header.
    [Export] public int listIndent = 12;

    // Bracket one property change as one undo step. The painter owns the
    // history; this owns the widgets.
    public System.Action BeforeEdit;
    public System.Action AfterEdit;

    // The world the open document authors (WorldMapData.World). A resource row
    // offers that world's own files and the unscoped ones — never another
    // world's (see WorldScope).
    public string World;

    // What the rows were built for. Rebuilding every frame would destroy the
    // widget being typed into, so the panel rebuilds only when the selection —
    // or the entry under it, which the first edit forks — actually changes.
    private EntityPlacement _shown;
    private SpawnEntryData _shownEntry;
    // The set the scatter listing was built for. Mutually exclusive with the
    // pair above: whichever mode is up, the other's fields are null.
    private SpawnScatterData _shownScatter;
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
        SpawnEntryData entry = placement?.Entry;
        if (entry == null)
        {
            HidePanel();
            return;
        }
        Visible = true;
        _shownScatter = null;
        if (ReferenceEquals(placement, _shown) && ReferenceEquals(entry, _shownEntry))
        {
            // Same rows, possibly different values — an undo changes what the
            // entry holds without touching which entry it is.
            if (titleLabel != null)
            {
                titleLabel.Text = placement.DisplayName();
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

    // The scatter set the tool has selected, or null for "nothing selected".
    // Called every frame, like Show.
    //
    // READ-ONLY, and that is not a gap to be filled later: a set is a shared
    // asset that several documents paint, so editing one HERE would silently
    // change every world using it — unlike a placement, which the first edit
    // forks into the document. What the panel is for is answering "how much of
    // what am I about to paint" without opening the .tres.
    public void ShowScatter(SpawnScatterData set)
    {
        if (set == null)
        {
            HidePanel();
            return;
        }
        Visible = true;
        if (ReferenceEquals(set, _shownScatter))
        {
            // Nothing here tracks a live value — the rows are the set's authored
            // rates, and a set is immutable while the game runs.
            return;
        }
        FlushPendingEdit();
        _shown = null;
        _shownEntry = null;
        _shownScatter = set;
        RebuildScatter();
    }

    // Empty the panel and take it off screen, whichever mode was up.
    private void HidePanel()
    {
        if (_shown != null || _shownScatter != null)
        {
            // Before the widgets go, and while _rowsOwner still names who they
            // belong to.
            FlushPendingEdit();
            _shown = null;
            _shownEntry = null;
            _shownScatter = null;
            Clear();
        }
        Visible = false;
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
            titleLabel.Text = _shown.DisplayName();
        }
        AddNameRow();
        foreach (Godot.Collections.Dictionary property in OrderedProperties(_shownEntry))
        {
            var name = new StringName(property["name"].AsString());
            AddRow(name, (Variant.Type)(long)property["type"],
                (PropertyHint)(long)property["hint"], property["hint_string"].AsString());
        }
        // A row's initial state comes from the SAME refresher that keeps it up
        // to date, so a row type cannot be built with one and forget the other —
        // which is what left every flags row reading as all-unchecked whatever
        // the entry held, and made a reselect look like the edit had reverted.
        RefreshRows();
    }

    // One row per spawn row: what it places, and how many of it a square
    // kilometre of ELIGIBLE ground gets.
    //
    // Per km² and not per m², because the authored unit (square metres between
    // spawns) is the inverse of what an author wants to know and lands in the
    // hundreds and thousands where a probability is 0.002. Eligible is the
    // caveat that cannot be dropped: the rate is rolled per QUALIFYING column,
    // so water, cliffs, roads and painted barriers are not in the km² — a
    // region's real count is this times its eligible fraction.
    //
    // Rows are listed densest first. The file's own order is authoring order
    // and says nothing; what an author is checking here is which creature
    // dominates.
    private void RebuildScatter()
    {
        Clear();
        if (rows == null || _shownScatter == null)
        {
            return;
        }
        if (titleLabel != null)
        {
            titleLabel.Text = _shownScatter.Label;
        }
        List<(string Name, string Rate)> listed = ScatterRows(_shownScatter);
        if (listed.Count == 0)
        {
            AddScatterRow("(empty)", "");
            return;
        }
        foreach ((string name, string rate) in listed)
        {
            AddScatterRow(name, rate);
        }
    }

    // The listing itself, densest first. Shared with worldmap_check so the
    // report is of the panel that will actually be built — a second copy of the
    // arithmetic is how a readout drifts from the thing it reports on.
    public static List<(string Name, string Rate)> ScatterRows(SpawnScatterData set)
    {
        var listed = new List<(string, string)>();
        foreach (SpawnListRow row in (set?.RowsFlat ?? System.Array.Empty<SpawnListRow>())
            .Where(r => r?.entry != null)
            .OrderByDescending(r => PerSquareKm(r.squareMetersPerSpawn)))
        {
            float perKm = PerSquareKm(row.squareMetersPerSpawn);
            listed.Add((SpawnEntryData.Describe(row.entry),
                // A rate of 0 keeps the row out of the area scan entirely, which
                // is a different statement from "very few" and reads as one.
                perKm <= 0f ? "never" : $"{perKm:N0} / km²"));
        }
        return listed;
    }

    // Expected spawns per square kilometre of eligible ground. The roll is
    // `density / squareMetersPerSpawn` per 1 m² column (WorldMapState.AreaRoll),
    // so a km² of it is that times a million. Reported at the set's OWN rate:
    // the brush's density multiplier scales every row equally and is on the HUD
    // beside it, and folding it in here would make the panel flicker as R/F is
    // held.
    private static float PerSquareKm(float squareMetersPerSpawn)
    {
        const float SquareMetersPerSquareKm = 1_000_000f;
        return squareMetersPerSpawn <= 0f ? 0f : SquareMetersPerSquareKm / squareMetersPerSpawn;
    }

    private void AddScatterRow(string name, string rate)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label
        {
            Text = name,
            CustomMinimumSize = new Vector2(labelWidth, 0f),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var value = new Label
        {
            Text = rate,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        value.AddThemeColorOverride("font_color", readOnlyColor);
        row.AddChild(value);
        rows.AddChild(row);
    }

    // The properties this entry shows, in the order it wants them. Shared with
    // worldmap_check so the report is of the panel that will actually be built —
    // a second copy of the filter is how the by-entry listing drifted before.
    public static List<Godot.Collections.Dictionary> OrderedProperties(SpawnEntryData entry)
    {
        var shown = new List<Godot.Collections.Dictionary>();
        if (entry == null)
        {
            return shown;
        }
        foreach (Godot.Collections.Dictionary property in entry.GetPropertyList())
        {
            // ScriptVariable is the flag Godot sets on a script's own exports, so
            // engine bookkeeping (resource_path, script) never reaches the panel.
            var usage = (PropertyUsageFlags)(long)property["usage"];
            if ((usage & PropertyUsageFlags.ScriptVariable) == 0)
            {
                continue;
            }
            if (entry.ShowsProperty(new StringName(property["name"].AsString())))
            {
                shown.Add(property);
            }
        }
        StringName[] order = entry.PropertyOrder;
        if (order == null || order.Length == 0)
        {
            return shown;
        }
        // A stable sort on "where does this name sit in the wanted order", with
        // everything unnamed sorting after in the declaration order it already
        // had. OrderBy is stable, which is what preserves that tail.
        return shown.OrderBy(p =>
        {
            string name = p["name"].AsString();
            int at = System.Array.FindIndex(order, n => n.ToString() == name);
            return at < 0 ? order.Length : at;
        }).ToList();
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
        EPropertyEditor kind = EditorFor(_shownEntry, name, type, hint, World, out Type resourceType,
            out string[] names, out Resource[] resources);
        if (kind == EPropertyEditor.List)
        {
            // A list takes the panel's whole width under its name: each element
            // is a block of rows of its own, and squeezed into the value column
            // those rows would have a label column a third the width of this one.
            var block = new VBoxContainer();
            block.AddChild(new Label { Text = name.ToString() });
            var indent = new MarginContainer();
            indent.AddThemeConstantOverride("margin_left", listIndent);
            indent.AddChild(BuildList(name, resourceType));
            block.AddChild(indent);
            rows.AddChild(block);
            return;
        }
        var row = new HBoxContainer();
        row.AddChild(new Label
        {
            Text = name.ToString(),
            CustomMinimumSize = new Vector2(labelWidth, 0f),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Control editor = BuildEditor(PropertyBinding(name), kind, type, hint, hintString,
            resourceType, names, resources);
        editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(editor);
        rows.AddChild(row);
    }

    // How the panel edits one property. Named rather than inlined into the
    // switch below so `worldmap_check` can report what an entry type exposes
    // without building any widgets — the question "what can I actually set on a
    // placement of this?" otherwise has no answer short of opening the painter
    // and clicking one.
    public enum EPropertyEditor
    {
        Text,
        Multiline,
        Check,
        Enum,
        Flags,
        Number,
        ResourcePick,
        NamePick,
        List,
        ReadOnly,
    }

    // The one classifier, shared by the row builder and the check. It takes the
    // ENTRY rather than its type because a name list depends on what this entry
    // NAMES — its descriptor's brain, its rig's animation library — not on the
    // class. `resourceType` and `names` belong to their own kinds and are null
    // for every other; for a List, `resourceType` is the element type.
    public static EPropertyEditor EditorFor(SpawnEntryData entry, StringName name,
        Variant.Type type, PropertyHint hint, string world, out Type resourceType, out string[] names,
        out Resource[] resources)
    {
        return Classify(entry?.GetType(), entry, name, type, hint, world,
            out resourceType, out names, out resources);
    }

    // The same question for one field of a list ELEMENT — a loot row's item or
    // count. Nothing constrains it (an element names no family), and a list
    // inside an element stays read-only: a list of lists is not a thing a
    // placement needs and not a thing this narrow panel can show.
    public static EPropertyEditor ElementEditorFor(Type element, StringName name,
        Variant.Type type, PropertyHint hint, string world, out Type resourceType)
    {
        return Classify(element, null, name, type, hint, world,
            out resourceType, out _, out _);
    }

    private static EPropertyEditor Classify(Type owner, SpawnEntryData entry, StringName name,
        Variant.Type type, PropertyHint hint, string world, out Type resourceType, out string[] names,
        out Resource[] resources)
    {
        resourceType = null;
        names = null;
        resources = null;
        switch (type)
        {
            case Variant.Type.String or Variant.Type.StringName:
                if (hint == PropertyHint.MultilineText)
                {
                    return EPropertyEditor.Multiline;
                }
                // A derivable set of valid values becomes a dropdown; anything
                // else stays free text.
                names = entry?.NameCandidates(name);
                return names != null && names.Length > 0
                    ? EPropertyEditor.NamePick : EPropertyEditor.Text;
            case Variant.Type.Bool:
                return EPropertyEditor.Check;
            case Variant.Type.Int when hint == PropertyHint.Enum:
                return EPropertyEditor.Enum;
            case Variant.Type.Int when hint == PropertyHint.Flags:
                return EPropertyEditor.Flags;
            case Variant.Type.Int or Variant.Type.Float:
                return EPropertyEditor.Number;
            case Variant.Type.Object:
                // The entry may constrain this to a FAMILY — a goblin entry
                // offers only goblins. Asked BEFORE the project-wide scan and
                // winning outright, because that constraint is the whole reason
                // the row is safe to show: an unconstrained descriptor picker
                // would let a fork become a spider while still being named, and
                // highlighted, as a goblin.
                resources = entry?.ResourceCandidates(name);
                if (resources is { Length: > 0 })
                {
                    return EPropertyEditor.ResourcePick;
                }
                resources = null;
                resourceType = ResourceFieldType(owner, name);
                // A picker with nothing to offer is a control that cannot change
                // the result, which is the same reason the cluster fields are
                // hidden — so it falls back to the read-only summary. That also
                // keeps an EMBEDDED value legible: every MobPalette in the
                // project is a sub-resource with no file to pick, and an empty
                // dropdown over one reads as "this field is unset".
                if (resourceType != null
                    && ResourceTypeIndex.Candidates(resourceType, world).Length > 0)
                {
                    return EPropertyEditor.ResourcePick;
                }
                resourceType = null;
                return EPropertyEditor.ReadOnly;
            case Variant.Type.Array when entry != null:
                resourceType = ListElementType(owner, name);
                return resourceType != null ? EPropertyEditor.List : EPropertyEditor.ReadOnly;
            default:
                return EPropertyEditor.ReadOnly;
        }
    }

    private Control BuildEditor(Binding binding, EPropertyEditor kind, Variant.Type type,
        PropertyHint hint, string hintString, Type resourceType, string[] names, Resource[] resources)
    {
        switch (kind)
        {
            case EPropertyEditor.Multiline:
                return BuildMultiline(binding);
            case EPropertyEditor.Text:
                return BuildLine(binding);
            case EPropertyEditor.Check:
                return BuildCheck(binding);
            case EPropertyEditor.Enum:
                return BuildEnum(binding, hintString);
            case EPropertyEditor.Flags:
                return BuildFlags(binding, hintString);
            case EPropertyEditor.Number:
                return BuildNumber(binding, type == Variant.Type.Int, hint, hintString);
            case EPropertyEditor.ResourcePick:
                return BuildResourcePicker(binding, resourceType, resources);
            case EPropertyEditor.NamePick:
                return BuildNamePicker(binding, names);
            default:
                return BuildReadOnly(binding);
        }
    }

    // Where one editor reads its value and where a new one goes. A property row
    // binds to a field of the placement's entry; a list row binds to one field
    // of one element. `Write` is handed the entry the placement OWNS — the edit
    // paths fork it first — so no binding can reach the shared palette file.
    private readonly struct Binding
    {
        public readonly Func<Variant> Read;
        public readonly Action<SpawnEntryData, Variant> Write;

        public Binding(Func<Variant> read, Action<SpawnEntryData, Variant> write)
        {
            Read = read;
            Write = write;
        }
    }

    // Reading goes through the placement, never through a captured entry
    // reference: the first edit REPLACES the entry with a fork and every row
    // must follow it there.
    private Binding PropertyBinding(StringName name)
    {
        return new Binding(
            () => _rowsOwner?.Entry != null ? _rowsOwner.Entry.Get(name) : default,
            (target, value) => target.Set(name, value));
    }

    // The placement's own copy of its entry, forking it on the first edit.
    private SpawnEntryData OwnEntry()
    {
        SpawnEntryData target = _rowsOwner.EditableEntry();
        if (ReferenceEquals(_rowsOwner, _shown))
        {
            _shownEntry = target;
        }
        return target;
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
    private void ApplyLive(Binding binding, Variant value)
    {
        if (_rowsOwner?.Entry == null || binding.Read().ToString() == value.ToString())
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
        binding.Write(OwnEntry(), value);
    }

    private void Commit(Binding binding, Variant value)
    {
        if (_rowsOwner?.Entry == null)
        {
            return;
        }
        // A row commits on losing focus as well as on Enter, so clicking into a
        // box and back out reaches here with the value it already had. Forking
        // the palette entry for that would silently stop the placement tracking
        // the palette, and it would cost an undo slot. Compared as TEXT because
        // Variant does not compare by value here — the undo aspect pays for the
        // same thing.
        if (binding.Read().ToString() == value.ToString())
        {
            return;
        }
        BeforeEdit?.Invoke();
        binding.Write(OwnEntry(), value);
        AfterEdit?.Invoke();
    }

    // A change that is not a value — adding or removing a list element — as one
    // undo step on the placement's own copy.
    private void Mutate(Action<SpawnEntryData> change)
    {
        if (_rowsOwner?.Entry == null)
        {
            return;
        }
        BeforeEdit?.Invoke();
        change(OwnEntry());
        AfterEdit?.Invoke();
    }

    // The PLACEMENT's name, first because it is not the entry's: it writes to
    // the placement itself, so naming a chest does not fork it off the palette.
    // Typed like any other text row — live, one undo step per run of typing.
    private void AddNameRow()
    {
        var edit = new LineEdit { PlaceholderText = "unnamed" };
        edit.TextChanged += _ => ApplyName(edit.Text);
        edit.TextSubmitted += _ => EndTyping();
        edit.FocusExited += () =>
        {
            ApplyName(edit.Text);
            EndTyping();
        };
        _refreshers.Add(() =>
        {
            if (!edit.HasFocus())
            {
                edit.Text = _rowsOwner?.name ?? "";
            }
        });
        var row = new HBoxContainer();
        row.AddChild(new Label
        {
            Text = EntityPlacement.PropertyName.name.ToString(),
            CustomMinimumSize = new Vector2(labelWidth, 0f),
            VerticalAlignment = VerticalAlignment.Center,
        });
        edit.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(edit);
        rows.AddChild(row);
    }

    private void ApplyName(string text)
    {
        if (_rowsOwner == null || (_rowsOwner.name ?? "") == text)
        {
            return;
        }
        if (!_typing)
        {
            BeforeEdit?.Invoke();
            _typing = true;
        }
        _rowsOwner.name = text;
    }

    private Control BuildLine(Binding binding)
    {
        var edit = new LineEdit();
        edit.TextChanged += _ => ApplyLive(binding, edit.Text);
        // Enter ENDS the undo step rather than committing the value — the value
        // is already in. Leaving the field applies once more first, since a paste
        // or an undo inside the box can move the text without a keystroke.
        edit.TextSubmitted += _ => EndTyping();
        edit.FocusExited += () =>
        {
            ApplyLive(binding, edit.Text);
            EndTyping();
        };
        _refreshers.Add(() =>
        {
            if (!edit.HasFocus())
            {
                edit.Text = binding.Read().AsString();
            }
        });
        return edit;
    }

    private Control BuildMultiline(Binding binding)
    {
        var edit = new TextEdit
        {
            CustomMinimumSize = new Vector2(0f, multilineHeight),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
        };
        edit.TextChanged += () => ApplyLive(binding, edit.Text);
        edit.FocusExited += () =>
        {
            ApplyLive(binding, edit.Text);
            EndTyping();
        };
        _refreshers.Add(() =>
        {
            if (!edit.HasFocus())
            {
                edit.Text = binding.Read().AsString();
            }
        });
        return edit;
    }

    private Control BuildCheck(Binding binding)
    {
        var check = new CheckBox();
        check.Toggled += on => Commit(binding, on);
        _refreshers.Add(() => check.SetPressedNoSignal(binding.Read().AsBool()));
        return check;
    }

    private Control BuildNumber(Binding binding, bool integer, PropertyHint hint, string hintString)
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
        spin.ValueChanged += v => Commit(binding, integer ? Variant.From((int)v) : Variant.From((float)v));
        _refreshers.Add(() =>
        {
            if (!spin.GetLineEdit().HasFocus())
            {
                spin.SetValueNoSignal(integer ? binding.Read().AsInt32() : binding.Read().AsSingle());
            }
        });
        return spin;
    }

    private Control BuildEnum(Binding binding, string hintString)
    {
        var option = new OptionButton();
        foreach ((string label, int value) in ParseHintItems(hintString, flags: false))
        {
            option.AddItem(label, value);
        }
        option.ItemSelected += index => Commit(binding, option.GetItemId((int)index));
        _refreshers.Add(() =>
        {
            int current = binding.Read().AsInt32();
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

    // A compact dropdown of checkable items, the same shape the Godot-side
    // editor uses for these properties (addons/data_ed/FlagsPropertyEditor,
    // opted into with [CompactFlags]). That one is an EditorProperty behind
    // `#if TOOLS` and cannot be instantiated in the running game, so this
    // mirrors its behaviour rather than sharing it — but the rules below are
    // ITS rules, and they should stay in step.
    //
    // A row of checkboxes was the first version. The value is a SET ("day AND
    // clear" is a normal thing to want from a chest), so checkboxes are honest,
    // but they cost a row as wide as the flag count on every entry that has any
    // — and the panel is a narrow strip beside the map.
    private Control BuildFlags(Binding binding, string hintString)
    {
        var button = new MenuButton
        {
            Alignment = HorizontalAlignment.Left,
            ClipText = true,
            // Bare keys belong to the painter, exactly as on the tool buttons.
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            // A MenuButton is FLAT by default, which draws it as bare text — beside
            // a property name that reads as a read-only value, and authors took the
            // knowledge stone's components for one. Framed, with the enum
            // dropdown's own arrow, it looks like the control it is.
            Flat = false,
            IconAlignment = HorizontalAlignment.Right,
        };
        // The default theme's arrow now, and again once the button can see the
        // panel's theme, so it is whatever the OptionButton rows beside it draw.
        button.Icon = button.GetThemeIcon("arrow", "OptionButton");
        button.ThemeChanged += () => button.Icon = button.GetThemeIcon("arrow", "OptionButton");
        PopupMenu menu = button.GetPopup();
        // The value is a set, so the menu stays open while several are toggled.
        menu.HideOnCheckableItemSelection = false;
        var bits = new List<(string Label, int Bit)>();
        foreach ((string label, int value) in ParseHintItems(hintString, flags: true))
        {
            // Skip the conventional `None = 0` and any MULTI-BIT alias (`All`).
            // Neither is independently togglable: `mask | 0` and `mask & ~0` are
            // both the mask, so a None item could never write anything, and an
            // alias item toggles several primaries at once while its own checked
            // state is ambiguous. Godot's own flags inspector hides them too.
            if (value <= 0 || (value & (value - 1)) != 0)
            {
                continue;
            }
            menu.AddCheckItem(label, value);
            bits.Add((label, value));
        }
        // IdPressed rather than IndexPressed: the id IS the bit, so nothing
        // depends on menu order.
        menu.IdPressed += id =>
        {
            int mask = binding.Read().AsInt32();
            var bit = (int)id;
            Commit(binding, (mask & bit) == bit ? mask & ~bit : mask | bit);
        };
        _refreshers.Add(() =>
        {
            int mask = binding.Read().AsInt32();
            string text = "";
            for (int i = 0; i < bits.Count; i++)
            {
                bool on = (mask & bits[i].Bit) == bits[i].Bit;
                // By INDEX — the items were added in this order.
                menu.SetItemChecked(i, on);
                if (on)
                {
                    text += text.Length > 0 ? $", {bits[i].Label}" : bits[i].Label;
                }
            }
            // Never blank: an empty button reads as a broken control rather than
            // as "no conditions", which is a meaningful and common value.
            button.Text = text.Length == 0 ? "None" : text;
        });
        return button;
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

    // The CLR type behind an exported Object field, or null where the panel
    // should leave it as a read-only row.
    //
    // Read off the entry's own C# type rather than parsed out of the property
    // hint: these ARE C# fields, so reflection is the exact answer, while a hint
    // string is the editor's rendering of it and is empty or a bare class name
    // depending on how the export was declared.
    //
    // Two exclusions, both deliberate. A PackedScene is a rig choice rather than
    // data — an NPC's `scene` has to gender-match its `outfit`, so offering every
    // scene in the project as a free pick invites a mismatch the panel cannot
    // check. Arrays never reach here (they are Variant.Type.Array) and are
    // classified as lists instead.
    private static Type ResourceFieldType(Type owner, StringName name)
    {
        if (owner == null)
        {
            return null;
        }
        string field = name.ToString();
        Type type = owner.GetField(field)?.FieldType
            ?? owner.GetProperty(field)?.PropertyType;
        if (type == null || !typeof(Resource).IsAssignableFrom(type)
            || typeof(PackedScene).IsAssignableFrom(type))
        {
            return null;
        }
        return type;
    }

    // The resources this field may be set to, plus an explicit empty.
    //
    // Two sources, and which one applies is the entry's call. `constrained` is a
    // set the entry itself named (a goblin's descriptors, an npc's appearances)
    // and is used verbatim. Otherwise every authored .tres of the
    // field's type, found by SCANNING rather than from a palette: a conversation
    // is authored as a file, and a registration step in a second resource is one
    // that gets forgotten.
    private Control BuildResourcePicker(Binding binding, Type type, Resource[] constrained = null)
    {
        var option = new OptionButton { ClipText = true };
        // A constrained candidate is already loaded; a scanned one is loaded
        // only when it is picked, so opening a panel never pulls in every
        // conversation in the project.
        string[] paths = constrained != null
            ? System.Array.ConvertAll(constrained, r => r?.ResourcePath ?? "")
            : ResourceTypeIndex.Candidates(type, World);
        // Index 0 is "none", so a field can always be cleared — an NPC with no
        // conversation is a real thing to author (Talk does nothing).
        option.AddItem("—", 0);
        for (int i = 0; i < paths.Length; i++)
        {
            // A constrained candidate without a file falls back to its resource
            // name, or the row would be blank and unpickable by sight.
            string label = paths[i].GetFile().GetBaseName();
            if (string.IsNullOrEmpty(label) && constrained != null)
            {
                label = constrained[i]?.ResourceName is { Length: > 0 } named
                    ? named : $"(unnamed {i + 1})";
            }
            option.AddItem(label, i + 1);
        }
        // A value the scan cannot name: a sub_resource embedded in the document
        // (an NPC's recolor palette) has no path to match against, and dropping
        // it into "—" would read as the field being empty and invite a pick that
        // silently discards it. Offered as its own entry instead, so leaving it
        // alone is what selecting it does.
        int embedded = option.ItemCount;
        option.AddItem("(embedded)", embedded);
        option.SetItemDisabled(option.GetItemIndex(embedded), true);

        option.ItemSelected += index =>
        {
            int id = option.GetItemId((int)index);
            if (id == embedded)
            {
                return;
            }
            Commit(binding, id == 0
                ? default
                : Variant.From(constrained != null
                    ? constrained[id - 1]
                    : GD.Load<Resource>(paths[id - 1])));
        };
        _refreshers.Add(() =>
        {
            var current = binding.Read().As<Resource>();
            int want = 0;
            if (current != null)
            {
                want = embedded;
                for (int i = 0; i < paths.Length; i++)
                {
                    // Reference first: a constrained candidate may be an
                    // embedded resource with no path, which would otherwise
                    // match every other pathless one.
                    if ((constrained != null && ReferenceEquals(constrained[i], current))
                        || (!string.IsNullOrEmpty(paths[i]) && paths[i] == current.ResourcePath))
                    {
                        want = i + 1;
                        break;
                    }
                }
            }
            // The embedded row exists only while something is actually in it,
            // or every cleared field carries a dead option.
            option.SetItemDisabled(option.GetItemIndex(embedded), want != embedded);
            option.Selected = option.GetItemIndex(want);
        });
        return option;
    }

    // A dropdown over the values the ENTRY says this name may take — a brain's
    // behaviour nodes, a rig's animation clips. Both fail silently when
    // mistyped (a bad behaviour name falls through to the species default, a bad
    // clip fails ModelAnimator.HasAnimation), which is exactly the case a
    // free-text box is worst at.
    //
    // The list is ADVISORY. Whatever the entry currently holds is offered even
    // when the candidates do not contain it, so a value authored against another
    // rig — or before a brain was retuned — is not silently rewritten by merely
    // selecting the placement. It is marked so the author can see it is adrift.
    private Control BuildNamePicker(Binding binding, string[] candidates)
    {
        var option = new OptionButton { ClipText = true };
        // Index 0 clears the field, which for both of these means "the species
        // default" and is a normal thing to author.
        option.AddItem("—", 0);
        for (int i = 0; i < candidates.Length; i++)
        {
            option.AddItem(candidates[i], i + 1);
        }
        // Appended lazily, and only while something is actually adrift.
        int foreign = candidates.Length + 1;
        option.AddItem("", foreign);

        option.ItemSelected += index =>
        {
            int id = option.GetItemId((int)index);
            if (id == foreign)
            {
                return;
            }
            Commit(binding, id == 0 ? "" : candidates[id - 1]);
        };
        _refreshers.Add(() =>
        {
            string current = binding.Read().AsString();
            int want = 0;
            if (!string.IsNullOrEmpty(current))
            {
                want = foreign;
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (candidates[i] == current)
                    {
                        want = i + 1;
                        break;
                    }
                }
            }
            int foreignAt = option.GetItemIndex(foreign);
            option.SetItemText(foreignAt, want == foreign ? $"{current}  (not in this rig)" : "");
            option.SetItemDisabled(foreignAt, want != foreign);
            option.Selected = option.GetItemIndex(want);
        });
        return option;
    }

    // What is left read-only after the identity rows are hidden is the set that
    // WOULD vary per placement and has no editor yet — a list of strings or
    // scenes, an embedded sub-resource nothing lists. It is marked rather than
    // merely dimmed, because a dimmed row reads as "this cannot change" when the
    // truth is "not here, not yet".
    private const string NO_EDITOR = "  ·  no editor yet";

    private Control BuildReadOnly(Binding binding)
    {
        var label = new Label { VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeColorOverride("font_color", readOnlyColor);
        _refreshers.Add(() => label.Text = Summarize(binding.Read()) + NO_EDITOR);
        return label;
    }

    // ---- Lists ---------------------------------------------------------------

    // The element type of an exported list the panel can edit — a C# array or a
    // Godot typed array of some Resource — or null. PackedScene is excluded for
    // ResourceFieldType's reason, and a string list is not a list of records.
    public static Type ListElementType(Type owner, StringName name)
    {
        Type type = owner?.GetField(name.ToString())?.FieldType;
        if (type == null)
        {
            return null;
        }
        Type element = type.IsArray
            ? type.GetElementType()
            : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Godot.Collections.Array<>)
                ? type.GetGenericArguments()[0]
                : null;
        if (element == null || !typeof(Resource).IsAssignableFrom(element)
            || typeof(PackedScene).IsAssignableFrom(element))
        {
            return null;
        }
        return element;
    }

    // Is a list's element a PICK of an authored file, or a record edited in
    // place? The same rule a single resource row follows: offer files when the
    // project has some of that type, and otherwise the element is the data.
    public static bool ListPicksFiles(Type element, string world)
        => ResourceTypeIndex.Candidates(element, world).Length > 0;

    // The list as it stands, copied out. Read off the C# field rather than
    // through Get, which would marshal a Godot array per call for a value that
    // is already managed.
    private static List<Resource> ReadList(SpawnEntryData entry, StringName name)
    {
        var items = new List<Resource>();
        if (entry?.GetType().GetField(name.ToString())?.GetValue(entry) is System.Collections.IEnumerable list)
        {
            foreach (object item in list)
            {
                items.Add(item as Resource);
            }
        }
        return items;
    }

    // Always a NEW collection, never the one the entry holds: a fork is shallow,
    // so that one may still be the palette file's, and the undo snapshot is
    // holding the old one to put back.
    private static void WriteList(SpawnEntryData entry, StringName name, List<Resource> items)
    {
        System.Reflection.FieldInfo field = entry.GetType().GetField(name.ToString());
        Type element = ListElementType(entry.GetType(), name);
        if (field == null || element == null)
        {
            return;
        }
        if (field.FieldType.IsArray)
        {
            System.Array array = System.Array.CreateInstance(element, items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                array.SetValue(items[i], i);
            }
            field.SetValue(entry, array);
            return;
        }
        object list = Activator.CreateInstance(field.FieldType);
        System.Reflection.MethodInfo add = field.FieldType.GetMethod("Add", new[] { element });
        foreach (Resource item in items)
        {
            add.Invoke(list, new object[] { item });
        }
        field.SetValue(entry, list);
    }

    // The fields an element of this type shows, off a throwaway instance —
    // there may be no element yet to ask. Cached per type: an element's exports
    // are fixed by its class.
    private static readonly Dictionary<Type, List<Godot.Collections.Dictionary>> _elementProperties = new();

    public static List<Godot.Collections.Dictionary> ElementProperties(Type element)
    {
        if (_elementProperties.TryGetValue(element, out List<Godot.Collections.Dictionary> cached))
        {
            return cached;
        }
        var shown = new List<Godot.Collections.Dictionary>();
        if (Activator.CreateInstance(element) is Resource probe)
        {
            foreach (Godot.Collections.Dictionary property in probe.GetPropertyList())
            {
                if (((PropertyUsageFlags)(long)property["usage"] & PropertyUsageFlags.ScriptVariable) != 0)
                {
                    shown.Add(property);
                }
            }
        }
        _elementProperties[element] = shown;
        return shown;
    }

    private Control BuildList(StringName name, Type element)
    {
        var box = new VBoxContainer();
        bool picks = ListPicksFiles(element, World);
        int built = ReadList(_rowsOwner?.Entry, name).Count;
        for (int i = 0; i < built; i++)
        {
            box.AddChild(picks ? BuildListPick(name, element, i) : BuildListRecord(name, element, i));
        }
        var add = new Button
        {
            Text = "+ add",
            // Bare keys belong to the painter, exactly as on the tool buttons.
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        add.Pressed += () => Mutate(target =>
        {
            List<Resource> items = ReadList(target, name);
            // A picked element starts empty and is chosen in its own row; a
            // record starts at its type's defaults (one of nothing, for loot).
            items.Add(picks ? null : (Resource)Activator.CreateInstance(element));
            WriteList(target, name, items);
        });
        box.AddChild(add);
        // An add, a remove, or an undo of either changes how many blocks there
        // should be, which a refresh cannot do — the rows are rebuilt instead.
        _refreshers.Add(() =>
        {
            if (ReadList(_rowsOwner?.Entry, name).Count != built)
            {
                RebuildDeferred();
            }
        });
        return box;
    }

    // One element that is a record: a header with its position and a remove
    // button, then its own fields through the same editors a property gets.
    private Control BuildListRecord(StringName list, Type element, int index)
    {
        var block = new VBoxContainer();
        block.AddChild(ListHeader(list, index, new Label
        {
            Text = $"#{index + 1}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        }));
        var fields = new VBoxContainer();
        foreach (Godot.Collections.Dictionary property in ElementProperties(element))
        {
            var field = new StringName(property["name"].AsString());
            var type = (Variant.Type)(long)property["type"];
            var hint = (PropertyHint)(long)property["hint"];
            EPropertyEditor kind = ElementEditorFor(element, field, type, hint, World, out Type resourceType);
            var row = new HBoxContainer();
            row.AddChild(new Label
            {
                Text = field.ToString(),
                CustomMinimumSize = new Vector2(labelWidth - listIndent, 0f),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Control editor = BuildEditor(ElementBinding(list, element, index, field), kind, type, hint,
                property["hint_string"].AsString(), resourceType, null, null);
            editor.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(editor);
            fields.AddChild(row);
        }
        var indent = new MarginContainer();
        indent.AddThemeConstantOverride("margin_left", listIndent);
        indent.AddChild(fields);
        block.AddChild(indent);
        return block;
    }

    // One element that is a pick of an authored file: the picker and its remove
    // button on one line.
    private Control BuildListPick(StringName list, Type element, int index)
    {
        Control picker = BuildResourcePicker(PickBinding(list, index), element);
        picker.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return ListHeader(list, index, picker);
    }

    private HBoxContainer ListHeader(StringName list, int index, Control lead)
    {
        var header = new HBoxContainer();
        header.AddChild(lead);
        var remove = new Button
        {
            Text = "×",
            TooltipText = "Remove",
            FocusMode = Control.FocusModeEnum.None,
        };
        remove.Pressed += () => Mutate(target =>
        {
            List<Resource> items = ReadList(target, list);
            if (index < items.Count)
            {
                items.RemoveAt(index);
                WriteList(target, list, items);
            }
        });
        header.AddChild(remove);
        return header;
    }

    // One field of one record. A write CLONES the element rather than setting
    // the field on it: the element may be the palette file's own (a fork is
    // shallow), and the undo snapshot compares elements by identity — mutated in
    // place, an edit would be invisible to undo and would retune every chest
    // still tracking the palette.
    private Binding ElementBinding(StringName list, Type element, int index, StringName field)
    {
        return new Binding(
            () =>
            {
                List<Resource> items = ReadList(_rowsOwner?.Entry, list);
                return index < items.Count && items[index] != null ? items[index].Get(field) : default;
            },
            (target, value) =>
            {
                List<Resource> items = ReadList(target, list);
                if (index >= items.Count)
                {
                    return;
                }
                Resource copy = items[index]?.Duplicate() as Resource
                    ?? (Resource)Activator.CreateInstance(element);
                copy.Set(field, value);
                items[index] = copy;
                WriteList(target, list, items);
            });
    }

    // The element itself, for a list of picks.
    private Binding PickBinding(StringName list, int index)
    {
        return new Binding(
            () =>
            {
                List<Resource> items = ReadList(_rowsOwner?.Entry, list);
                return index < items.Count && items[index] != null ? Variant.From(items[index]) : default;
            },
            (target, value) =>
            {
                List<Resource> items = ReadList(target, list);
                if (index < items.Count)
                {
                    items[index] = value.As<Resource>();
                    WriteList(target, list, items);
                }
            });
    }

    // Rebuild the rows once, after the current signal has finished — a list's
    // own add button is what triggers it, and freeing a button inside its own
    // Pressed is asking for trouble.
    private bool _rebuildQueued;

    private void RebuildDeferred()
    {
        if (_rebuildQueued)
        {
            return;
        }
        _rebuildQueued = true;
        Callable.From(() =>
        {
            _rebuildQueued = false;
            if (_shown?.Entry == null)
            {
                return;
            }
            FlushPendingEdit();
            _shownEntry = _shown.Entry;
            Rebuild();
        }).CallDeferred();
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
