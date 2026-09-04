using Godot;

// A spoken / written language in the world. Mobs reference one as their
// native tongue (drives chatter localization + comprehension gating); signposts
// reference one as the language their text is written in. The shared resource
// instance is the key used in Player.LearnedLanguages, so two mobs that share
// a LanguageData are mutually intelligible to a player who has learned it.
// [Tool] because ItemEvent is [Tool] and holds one of these — see the
// [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class LanguageData : Resource
{
    // Stable internal identifier, and the token an inline [lang:<id>] span
    // in authored text names (see LanguageText). Kept separate from
    // displayName because that is player-facing text and also seeds the
    // scrambler's cipher — renaming it must not silently re-point every
    // authored span. Registered on SimData.languages, which is what
    // resolves the token.
    [Export] public StringName id;

    // Text shown wherever the language is named in UI (learned-language list,
    // tooltip on unreadable signpost text, etc). Same StringName-as-display-
    // text convention as RegionData.displayName.
    [Export] public StringName displayName;
}
