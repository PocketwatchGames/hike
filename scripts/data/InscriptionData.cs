using Godot;

// What a signpost or a knowledge stone SAYS: the words and the tongue they are
// written in, which are one decision. Its own file so a placement PICKS it (the
// painter's property panel offers every one in scope) rather than typing text
// into a fork, and so one inscription can stand in several places. A world's own
// inscriptions live in worlds/<world>/inscriptions/, ones several worlds or the
// generator use in worlds/shared/inscriptions/.
//
// The words themselves are a loc key: the text lives in the owning world
// folder's strings, worlds/<world>/localization/<language>.tsv (worlds/shared/
// for the shared ones), keyed inscription_<file basename>. It may carry [lang:],
// [narrate] and [input:] markup like any authored line.
[GlobalClass]
public partial class InscriptionData : Resource
{
    [Export] public StringName textKey;
    // Null = legible to everyone.
    [Export] public LanguageData language;

    public bool HasText()
    {
        return textKey is not null && !textKey.IsEmpty;
    }

    // The raw authored line, markup intact, in the current UI language.
    public string LookUpText()
    {
        return HasText() ? Loc.Get(textKey) : string.Empty;
    }
}
