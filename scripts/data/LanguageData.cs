using System.Collections.Generic;
using Godot;

// A spoken / written language in the world. Mobs reference one as their
// native tongue (drives chatter localization + comprehension gating); signposts
// reference one as the language their text is written in. The shared resource
// instance is the key used in Knowledge.LearnedLessons, so two mobs that share
// a LanguageData are mutually intelligible to a player who has learned it.
// [Tool] because ItemEvent is [Tool] and holds one of these — see the
// [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class LanguageData : Resource
{
    // A language is learned in LESSONS, and a lesson is its SOURCE — the
    // inscription, scroll or action that teaches it, naming how much of the
    // language (a percent). The party remembers each source it has learned
    // from (Player.LessonSourceKey), so one taught twice — two copies of a
    // scroll, or banked and field knowledge together — counts once. The amount
    // LEARNED is the sum, capped at 100%; the curves below turn it into what
    // the player can read. FULL_PERCENT is "the whole tongue".
    public const float FULL_PERCENT = 100f;

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

    // How the language opens up: each maps the amount learned (x, 0..1) to
    // that aspect's coverage (y, 0..1). Unset = linear.
    [ExportGroup("Learning Curves")]
    // Share of words recognized.
    [Export] public Curve vocabularyCurve;
    // Share of word positions that hold their place.
    [Export] public Curve grammarCurve;
    // Share of numbers readable.
    [Export] public Curve numbersCurve;

    // Managed memo per amount learned — text renders and conversation
    // visibility ask this per line, a Curve sample is a native call, and the
    // amounts a player can hold are few. Safe without invalidation because
    // *Data is immutable after load.
    private Dictionary<float, LanguageCoverage> _coverageMemo;

    // `learned` in [0, 1]; 1 is fluency, whatever the curves end on.
    public LanguageCoverage ComputeCoverage(float learned)
    {
        if (learned >= 1f)
        {
            return LanguageCoverage.Full;
        }
        _coverageMemo ??= new Dictionary<float, LanguageCoverage>();
        if (_coverageMemo.TryGetValue(learned, out LanguageCoverage cached))
        {
            return cached;
        }
        LanguageCoverage coverage = SampleCoverage(learned);
        _coverageMemo[learned] = coverage;
        return coverage;
    }

    // ComputeCoverage without the memo, for a CONTINUOUS amount — an animated
    // meter passes a new value every frame, which would grow the memo forever.
    public LanguageCoverage SampleCoverage(float learned)
    {
        if (learned >= 1f)
        {
            return LanguageCoverage.Full;
        }
        return new LanguageCoverage(
            SampleCurve(vocabularyCurve, learned),
            SampleCurve(grammarCurve, learned),
            SampleCurve(numbersCurve, learned));
    }

    static float SampleCurve(Curve curve, float progress)
    {
        return curve == null ? progress : Mathf.Clamp(curve.SampleBaked(progress), 0f, 1f);
    }
}
