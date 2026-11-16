using Godot;

// What a signpost or a knowledge stone SAYS: the words and the tongue they are
// written in, which are one decision. Its own file so a placement PICKS it (the
// painter's property panel offers every one in scope) rather than typing text
// into a fork, and so one inscription can stand in several places. A world's own
// words live in worlds/<world>/inscriptions/, ones several worlds or the
// generator use in worlds/shared/inscriptions/. The text may carry [lang:] and
// [narrate] spans like any authored line.
[GlobalClass]
public partial class InscriptionData : Resource
{
    [Export(PropertyHint.MultilineText)] public string text = "";
    // Null = legible to everyone.
    [Export] public LanguageData language;
}
