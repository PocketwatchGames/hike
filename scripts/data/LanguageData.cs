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

    // First codepoint of this language's own script in assets/fonts/alien_glyphs.ttf:
    // 36 consecutive glyphs, 26 letters then 10 numerals. TextScrambler emits these
    // instead of Latin for anything the player can't read, and the UI font reaches
    // them through its `fallbacks` (resources/gui/ui_font.tres) — which is why no
    // display path has to know a language has a script of its own.
    // 0 means the language scrambles in Latin letters and needs no glyph art.
    // Authored text is never stored in these codepoints; only rendered output.
    [Export] public int glyphBase;
}
