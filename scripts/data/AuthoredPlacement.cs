using Godot;

// One authored placement of a spawn entry: the palette entry it came from, plus
// this placement's private copy once something on it has been edited. Shared by
// the painter's hand placements (EntityPlacement) and the entities a scene is
// built from (SceneEntityPlacement), so both fork, match and name the same way.
[GlobalClass]
public partial class AuthoredPlacement : Resource, IEntryEditTarget
{
    // The palette entry this placement was made from. NEVER replaced — a fork
    // goes beside it, in `custom`, so the link to the palette survives being
    // customized. Reconstructing that link afterwards was tried and cannot be
    // made to work: with the reference overwritten, the only thing left saying
    // where a fork came from was a name, and a name drifts.
    //
    // Null for an entry no palette offers, which a tool authors whole (a roof:
    // its style and footprint are the whole of it). Such a placement holds only
    // `custom`.
    [Export] public SpawnEntryData source;

    // This placement's private copy of `source`, once something on it has been
    // edited. Null until then, which is what makes an uncustomized placement
    // track whatever the palette entry is retuned to.
    [Export] public SpawnEntryData custom;

    // What this placement actually spawns: its own copy once it has one, else
    // the shared palette entry. Every reader wants this; only the palette
    // MEMBERSHIP questions read `source`.
    public SpawnEntryData Entry => custom ?? source;

    // Has this placement been edited away from its palette entry? A customized
    // one still belongs to the palette entry it came from — it is the one most
    // worth finding — so this marks it rather than un-matching it.
    public bool IsCustomized => custom != null && source != null;

    // Was this placement made from that palette entry? Reference equality, so a
    // fork answers exactly as its original does and a renamed palette file
    // changes nothing.
    public bool IsFrom(SpawnEntryData paletteEntry)
        => source != null && paletteEntry != null && source == paletteEntry;

    public virtual string DisplayName() => Label();

    // What this placement IS: named off the SOURCE palette entry but varied by
    // the live one — a fork keeps the name of the file it came from, and says
    // which member of that family this individual is. Never off the fork's own
    // path, which after a reload is <container>.tres::<id>.
    public string Label()
    {
        string label = source != null ? SpawnEntryData.PaletteName(source) : SpawnEntryData.TypeLabel(custom);
        string variant = Entry?.VariantName();
        if (!string.IsNullOrEmpty(variant) && variant != label)
        {
            label = $"{label}: {variant}";
        }
        return IsCustomized ? $"{label} *" : label;
    }

    // This placement's entry, ready to be EDITED.
    //
    // Copy-on-write: the first edit forks `source` into `custom`, which — having
    // no resource path — saves INTO the document holding this placement as a
    // sub-resource belonging to it alone. Shallow on purpose: a forked signpost
    // still references the same LanguageData rather than getting a private copy.
    public SpawnEntryData EditableEntry()
    {
        if (custom != null || source == null)
        {
            return Entry;
        }
        SpawnEntryData copy = source.Fork();
        if (ReferenceEquals(copy, source))
        {
            return source;
        }
        custom = copy;
        return custom;
    }
}
