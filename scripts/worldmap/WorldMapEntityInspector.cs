using System;
using Godot;
using System.Collections.Generic;
using System.Linq;

// Property editor for the entity tool's selected placement, top-right of the
// painter.
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
// What stays a read-only row: ARRAYS (an outfit, a stock list, loyalty gifts),
// which need list editing rather than a single pick, and PackedScene, which is a
// rig choice rather than data. The row is there rather than hidden so the panel
// never implies the entry holds less than it does.
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
        SpawnEntryData entry = placement?.Entry;
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
        ReadOnly,
    }

    // The one classifier, shared by the row builder and the check. It takes the
    // ENTRY rather than its type because a name list depends on what this entry
    // NAMES — its descriptor's brain, its rig's animation library — not on the
    // class. `resourceType` and `names` belong to their own kinds and are null
    // for every other.
    public static EPropertyEditor EditorFor(SpawnEntryData entry, StringName name,
        Variant.Type type, PropertyHint hint, out Type resourceType, out string[] names,
        out Resource[] resources)
    {
        resourceType = null;
        names = null;
        resources = null;
        Type owner = entry?.GetType();
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
                    && ResourceTypeIndex.Candidates(resourceType).Length > 0)
                {
                    return EPropertyEditor.ResourcePick;
                }
                resourceType = null;
                return EPropertyEditor.ReadOnly;
            default:
                return EPropertyEditor.ReadOnly;
        }
    }

    private Control BuildEditor(StringName name, Variant.Type type, PropertyHint hint, string hintString)
    {
        switch (EditorFor(_shownEntry, name, type, hint, out Type resourceType,
            out string[] names, out Resource[] resources))
        {
            case EPropertyEditor.Multiline:
                return BuildMultiline(name);
            case EPropertyEditor.Text:
                return BuildLine(name);
            case EPropertyEditor.Check:
                return BuildCheck(name);
            case EPropertyEditor.Enum:
                return BuildEnum(name, hintString);
            case EPropertyEditor.Flags:
                return BuildFlags(name, hintString);
            case EPropertyEditor.Number:
                return BuildNumber(name, type == Variant.Type.Int, hint, hintString);
            case EPropertyEditor.ResourcePick:
                return BuildResourcePicker(name, resourceType, resources);
            case EPropertyEditor.NamePick:
                return BuildNamePicker(name, names);
            default:
                return BuildReadOnly(name);
        }
    }

    // Reading and writing always go through the placement, never through a
    // captured entry reference: the first edit REPLACES the entry with a fork and
    // every row must follow it there.
    private Variant Read(StringName name)
    {
        return _rowsOwner?.Entry != null ? _rowsOwner.Entry.Get(name) : default;
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
        SpawnEntryData current = _rowsOwner?.Entry;
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
        SpawnEntryData current = _rowsOwner?.Entry;
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
    private Control BuildFlags(StringName name, string hintString)
    {
        var button = new MenuButton
        {
            Alignment = HorizontalAlignment.Left,
            ClipText = true,
            // Bare keys belong to the painter, exactly as on the tool buttons.
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
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
            int mask = Read(name).AsInt32();
            var bit = (int)id;
            Commit(name, (mask & bit) == bit ? mask & ~bit : mask | bit);
        };
        _refreshers.Add(() =>
        {
            int mask = Read(name).AsInt32();
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
    // check. Arrays never reach here (they are Variant.Type.Array) and want list
    // editing, not one pick.
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
    private Control BuildResourcePicker(StringName name, Type type, Resource[] constrained = null)
    {
        var option = new OptionButton { ClipText = true };
        // A constrained candidate is already loaded; a scanned one is loaded
        // only when it is picked, so opening a panel never pulls in every
        // conversation in the project.
        string[] paths = constrained != null
            ? System.Array.ConvertAll(constrained, r => r?.ResourcePath ?? "")
            : ResourceTypeIndex.Candidates(type);
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
            Commit(name, id == 0
                ? default
                : Variant.From(constrained != null
                    ? constrained[id - 1]
                    : GD.Load<Resource>(paths[id - 1])));
        };
        _refreshers.Add(() =>
        {
            var current = Read(name).As<Resource>();
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
    private Control BuildNamePicker(StringName name, string[] candidates)
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
            Commit(name, id == 0 ? "" : candidates[id - 1]);
        };
        _refreshers.Add(() =>
        {
            string current = Read(name).AsString();
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

    // What is left read-only after the identity rows are hidden is exactly the
    // set that WOULD vary per placement and has no editor yet — a chest's loot,
    // an NPC's stock / gifts / taste rules, all of which need list editing. It
    // is marked rather than merely dimmed, because a dimmed row reads as "this
    // cannot change" when the truth is "not here, not yet".
    private const string NO_EDITOR = "  ·  no editor yet";

    private Control BuildReadOnly(StringName name)
    {
        var label = new Label { VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeColorOverride("font_color", readOnlyColor);
        _refreshers.Add(() => label.Text = Summarize(Read(name)) + NO_EDITOR);
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
