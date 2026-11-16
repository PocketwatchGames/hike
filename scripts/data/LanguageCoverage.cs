// How much of each aspect of a language the player has, each in [0, 1].
// TextScrambler reads it per word, per word position and per number, by
// comparing a stable hash against the coverage — so anything revealed at 50%
// is still revealed at 80%.
public readonly struct LanguageCoverage
{
    public readonly float vocabulary;
    public readonly float grammar;
    public readonly float numbers;

    public LanguageCoverage(float vocabulary, float grammar, float numbers)
    {
        this.vocabulary = vocabulary;
        this.grammar = grammar;
        this.numbers = numbers;
    }

    public static readonly LanguageCoverage Full = new LanguageCoverage(1f, 1f, 1f);
    public static readonly LanguageCoverage None = new LanguageCoverage(0f, 0f, 0f);

    public bool IsFull => vocabulary >= 1f && grammar >= 1f && numbers >= 1f;
}
