// How much of each aspect of a language the player has, each in [0, 1].
// TextScrambler reads it per word, per word position, per number and per glyph
// of the language's script, by comparing a stable hash against the coverage —
// so anything revealed at 50% is still revealed at 80%.
public readonly struct LanguageCoverage
{
    public readonly float vocabulary;
    public readonly float grammar;
    public readonly float numbers;
    // Share of the script's glyphs the player can transliterate. A known glyph
    // draws as the Latin letter it stands for in the cipher — the word is still
    // gibberish, but in letters the player can sound out.
    public readonly float glyphs;

    public LanguageCoverage(float vocabulary, float grammar, float numbers, float glyphs)
    {
        this.vocabulary = vocabulary;
        this.grammar = grammar;
        this.numbers = numbers;
        this.glyphs = glyphs;
    }

    public static readonly LanguageCoverage Full = new LanguageCoverage(1f, 1f, 1f, 1f);
    public static readonly LanguageCoverage None = new LanguageCoverage(0f, 0f, 0f, 0f);

    public bool IsFull => vocabulary >= 1f && grammar >= 1f && numbers >= 1f && glyphs >= 1f;
}
