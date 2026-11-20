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
// [narrate] and [input:] markup like any authored line. An optional title,
// keyed inscription_<file basename>_title in the same file, heads it where it is
// read as a page (a book) - written in the same language, so it translates with
// the prose.
//
// Reading an inscription teaches `percent` of the language it is written in —
// a signpost, a knowledge stone and a book alike. The lesson is this FILE, so
// one inscription teaches once however many places it stands. 0 (the default)
// teaches nothing: most text is just text.
[GlobalClass]
public partial class InscriptionData : Resource
{
    [Export] public StringName textKey;
    // Optional; unset = no title.
    [Export] public StringName titleKey;
    // Null = legible to everyone.
    [Export] public LanguageData language;
    [Export(PropertyHint.Range, "0,100,1")] public float percent;

    public bool HasText()
    {
        return textKey is not null && !textKey.IsEmpty;
    }

    public bool TeachesLanguage() => language != null && percent > 0f;

    // Teaches this inscription's lesson; true when it was new.
    public bool Teach(Player player)
    {
        return TeachesLanguage() && player != null && player.LearnLanguageLesson(language, this, percent);
    }

    public bool IsLearned(Player player)
    {
        return !TeachesLanguage() || (player != null && player.GetLessonPercent(language, this) >= percent);
    }

    public bool HasTitle()
    {
        return titleKey is not null && !titleKey.IsEmpty;
    }

    // The raw authored title, markup intact; empty when there is none.
    public string LookUpTitle()
    {
        return HasTitle() ? Loc.Get(titleKey) : string.Empty;
    }

    // The raw authored line, markup intact, in the current UI language.
    public string LookUpText()
    {
        return HasText() ? Loc.Get(textKey) : string.Empty;
    }
}
