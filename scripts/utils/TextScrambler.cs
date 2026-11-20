using System;
using System.Collections.Generic;
using System.Text;

// Renders text the player can only partially read, given how much of each
// aspect of the language they have (LanguageCoverage). Called at display time;
// the underlying string data stays intact. Same input + same language + same
// coverage always produces the same gibberish, so re-reading the same line
// looks identical.
//
// Unreadable text is rewritten into the language's OWN script when it has a
// LanguageData.glyphBase — see CipherLetter. Nothing downstream knows: the
// codepoints land in a private-use block that the UI font reaches through its
// `fallbacks`, so a half-understood line renders Latin and alien glyphs side by
// side in one plain Label.
//
// Everything goes through a ScramblePlan — the line tokenized and hashed once —
// and a ScrambleState, what that plan looks like at one coverage. Scramble is
// the flattened state; TranslationAnimator steps between two states, so the
// two can never disagree about what a line reads as.
public static class TextScrambler
{
    // Layout of a LanguageData.glyphBase block: 26 letter glyphs then 10
    // numeral glyphs, matching asset_src/fonts/build_alien_glyphs.py.
    public const int GlyphLetterCount = 26;
    public const int GlyphNumeralCount = 10;

    // Renders `text` as a player with `coverage` of `language` reads it. Each
    // aspect is decided by a stable hash compared against its coverage, so
    // the same line reads the same way every time and anything revealed stays
    // revealed as coverage grows:
    //   Vocabulary — per WORD (case-insensitive, letters only, so "Hello," and
    //                "hello" are one word). An unknown word's letters run
    //                through the per-language substitution cipher.
    //   Numbers    — per NUMBER (its digit string). An unknown number's digits
    //                become the language's own numeral glyphs (or a stable
    //                letter, for a language with no script). Independent of the
    //                word: "12 stones" can read as a known word and an
    //                unreadable number.
    //   Grammar    — per word POSITION. A position under the coverage holds its
    //                word, punctuation and capitals. The rest lose punctuation
    //                and case (standalone punctuation tokens drop out), and
    //                their words are rotated among the loose positions so none
    //                lands back where it started. Whitespace runs stay in place
    //                so line breaks survive.
    // Null language, empty text, or full coverage returns `text` unchanged.
    public static string Scramble(string text, LanguageData language, LanguageCoverage coverage)
    {
        if (language == null || coverage.IsFull || string.IsNullOrEmpty(text))
        {
            return text;
        }
        ScramblePlan plan = new ScramblePlan(text, language);
        return plan.Flatten(plan.StateAt(coverage));
    }

    // Player's comprehension of `text` in `language` as a value in [0, 1]:
    //   translatedPct — share of word tokens whose word AND number (if any)
    //                   are known.
    //   orderPct      — share of word tokens sitting at their original
    //                   position: the held ones, plus a lone loose word, which
    //                   has nowhere else to go.
    // final = translatedPct × ((1 - grammarWeight) + orderPct × grammarWeight).
    // Both run over the same token list Scramble shows, after loose
    // punctuation drops out. Drives the soft gate on ConversationResponse
    // visibility (see ConversationVisibility).
    public static float ComputeComprehension(string text, LanguageData language, LanguageCoverage coverage, float grammarWeight)
    {
        return ComputeComprehension(text, language, coverage, grammarWeight, out _);
    }

    // As above, additionally reporting the number of word tokens the score
    // was averaged over. LanguageText weights each span of a multi-language
    // line by this, so a one-word span in a second tongue counts for one word
    // and not for half the line.
    public static float ComputeComprehension(string text, LanguageData language, LanguageCoverage coverage, float grammarWeight, out int weight)
    {
        weight = 0;
        if (string.IsNullOrEmpty(text))
        {
            return 1f;
        }
        ScramblePlan plan = new ScramblePlan(text, language);
        weight = plan.Count;
        if (language == null || plan.Count == 0 || coverage.IsFull)
        {
            return 1f;
        }

        ScrambleState state = plan.StateAt(coverage);
        int shown = 0;
        int translated = 0;
        int heldCount = 0;
        for (int k = 0; k < plan.Count; k++)
        {
            if (state.Dropped(k))
            {
                continue;
            }
            shown++;
            bool letterOK = !plan.HasLetter(k) || state.wordKnown[k];
            bool digitOK = !plan.HasDigit(k) || state.numberKnown[k];
            if (letterOK && digitOK)
            {
                translated++;
            }
            if (state.held[k])
            {
                heldCount++;
            }
        }
        weight = shown;
        if (shown == 0)
        {
            return 1f;
        }
        int inPlace = shown - heldCount == 1 ? heldCount + 1 : heldCount;
        float translatedPct = (float)translated / shown;
        float orderPct = (float)inPlace / shown;
        return translatedPct * ((1f - grammarWeight) + orderPct * grammarWeight);
    }

    // Salts keep the three per-aspect hashes independent of one another, so
    // knowing a word says nothing about whether its position holds.
    const uint WORD_SALT = 0x9E3779B9u;
    const uint NUMBER_SALT = 0x85EBCA6Bu;
    const uint POSITION_SALT = 0xC2B2AE35u;
    const int DIGIT_PERM_SALT = 0x55AA55AA;
    const int ROTATE_SALT = unchecked((int)0xDEADBEEF);

    static float WordUnit(string token, int seed)
    {
        uint h = (uint)seed ^ WORD_SALT;
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (!char.IsLetter(c)) { continue; }
            h = unchecked(h * 31u + char.ToLowerInvariant(c));
        }
        return Unit(h);
    }

    static float NumberUnit(string token, int seed)
    {
        uint h = (uint)seed ^ NUMBER_SALT;
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (c < '0' || c > '9') { continue; }
            h = unchecked(h * 31u + c);
        }
        return Unit(h);
    }

    static float PositionUnit(int index, int seed)
    {
        return Unit(unchecked((uint)seed ^ POSITION_SALT ^ ((uint)index * 0x27D4EB2Fu)));
    }

    // Whether a hashed aspect is known at `coverage`; full coverage knows all.
    static bool Known(float unit, float coverage)
    {
        return coverage >= 1f || unit < coverage;
    }

    // Avalanche (murmur3 finalizer) then the top 24 bits as [0, 1). The raw
    // polynomial hashes above are far from uniform in their high bits.
    static float Unit(uint h)
    {
        h ^= h >> 16;
        h = unchecked(h * 0x7FEB352Du);
        h ^= h >> 15;
        h = unchecked(h * 0x846CA68Bu);
        h ^= h >> 16;
        return (h >> 8) / 16777216f;
    }

    // Walk the string splitting into alternating whitespace runs and
    // non-whitespace word tokens. Always emits separators.Count == tokens.Count + 1
    // (leading/trailing entries may be empty) so a flatten can interleave them
    // back into the original shape.
    static void Tokenize(string text, List<string> tokens, List<string> separators)
    {
        int i = 0;
        int n = text.Length;
        StringBuilder sb = new StringBuilder();
        while (i < n && char.IsWhiteSpace(text[i]))
        {
            sb.Append(text[i]);
            i++;
        }
        separators.Add(sb.ToString());
        sb.Clear();
        while (i < n)
        {
            while (i < n && !char.IsWhiteSpace(text[i]))
            {
                sb.Append(text[i]);
                i++;
            }
            tokens.Add(sb.ToString());
            sb.Clear();
            while (i < n && char.IsWhiteSpace(text[i]))
            {
                sb.Append(text[i]);
                i++;
            }
            separators.Add(sb.ToString());
            sb.Clear();
        }
    }

    // A loose token loses its punctuation and case — those belong to the
    // structure grammar teaches ("don't" → "dont", "Hello," → "hello",
    // "1,000" → "1000"). One that was nothing but punctuation ("—", "...")
    // comes back empty and drops out of the line.
    static string Loosen(string token)
    {
        StringBuilder sb = new StringBuilder(token.Length);
        for (int j = 0; j < token.Length; j++)
        {
            char c = token[j];
            // Letter-or-digit in ANY script, not just ASCII: the test here
            // decides what survives, so an ASCII-only one silently deletes
            // every accented or CJK character in a localized line.
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    // Any script's letters count, not just ASCII — an accented or CJK word is
    // still a word the vocabulary cipher has to be able to hide.
    static bool HasLetterIn(string token)
    {
        for (int i = 0; i < token.Length; i++)
        {
            if (char.IsLetter(token[i]))
            {
                return true;
            }
        }
        return false;
    }

    static bool HasDigitIn(string token)
    {
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (c >= '0' && c <= '9')
            {
                return true;
            }
        }
        return false;
    }

    // Knuth multiplicative hash, folded into a letter slot. Stable across runs
    // and platforms for the same reason StableSeed is hand-rolled.
    static int HashToSlot(char c)
    {
        uint h = unchecked((uint)c * 2654435761u);
        return (int)((h >> 13) % GlyphLetterCount);
    }

    // glyphBase blocks may sit above the BMP — the supplementary private use
    // areas are the collision-safe place for them if a primary UI font ever
    // claims codepoints in U+E000..U+F8FF, which CJK fonts sometimes do.
    public static string GlyphString(int codepoint)
    {
        return codepoint <= char.MaxValue ? ((char)codepoint).ToString() : char.ConvertFromUtf32(codepoint);
    }

    static int[] BuildPermutation(int seed, int size)
    {
        Random rng = new Random(seed);
        int[] p = new int[size];
        for (int i = 0; i < size; i++)
        {
            p[i] = i;
        }
        for (int i = size - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        return p;
    }

    // Stable across runs and platforms (unlike string.GetHashCode, which
    // .NET randomizes per process). Keeps a given language's gibberish
    // visually consistent across save/reload. Also reused by
    // ConversationVisibility to seed the per-response visibility roll.
    public static int StableSeed(string s)
    {
        int h = 0;
        for (int i = 0; i < s.Length; i++)
        {
            h = unchecked(h * 31 + s[i]);
        }
        return h;
    }

    // One line in one language, tokenized and hashed once. A TOKEN is a
    // whitespace-delimited run of the original text, indexed k; it is also the
    // SLOT it was authored in, so a token is "home" when it sits in slot k. A
    // token that drops out (loose punctuation) leaves its slot empty, which
    // reassembles exactly as the separators around it merging.
    public sealed class ScramblePlan
    {
        public readonly LanguageData Language;
        public readonly int Count;
        readonly string[] _original;
        readonly string[] _loose;
        readonly string[] _separators;
        readonly float[] _wordUnit;
        readonly float[] _numberUnit;
        readonly float[] _positionUnit;
        readonly bool[] _hasLetter;
        readonly bool[] _hasDigit;
        readonly int[] _letterPerm;
        readonly int[] _digitPerm;
        readonly int _glyphBase;
        readonly int _rotateSeed;

        public ScramblePlan(string text, LanguageData language)
        {
            Language = language;
            List<string> tokens = new List<string>();
            List<string> separators = new List<string>();
            Tokenize(text ?? string.Empty, tokens, separators);
            Count = tokens.Count;
            _original = tokens.ToArray();
            _separators = separators.ToArray();
            _loose = new string[Count];
            _wordUnit = new float[Count];
            _numberUnit = new float[Count];
            _positionUnit = new float[Count];
            _hasLetter = new bool[Count];
            _hasDigit = new bool[Count];
            int seed = language == null ? 0 : StableSeed(language.displayName.ToString());
            _letterPerm = BuildPermutation(seed, GlyphLetterCount);
            // Digit substitution uses an independent permutation so it isn't
            // trivially derivable from the letter cipher.
            _digitPerm = BuildPermutation(seed ^ DIGIT_PERM_SALT, GlyphNumeralCount);
            _glyphBase = language?.glyphBase ?? 0;
            _rotateSeed = seed ^ ROTATE_SALT;
            for (int k = 0; k < Count; k++)
            {
                string tok = _original[k];
                _loose[k] = Loosen(tok);
                _wordUnit[k] = WordUnit(tok, seed);
                _numberUnit[k] = NumberUnit(tok, seed);
                _positionUnit[k] = PositionUnit(k, seed);
                _hasLetter[k] = HasLetterIn(tok);
                _hasDigit[k] = HasDigitIn(tok);
            }
        }

        public bool HasLetter(int k) => _hasLetter[k];
        // The grammar coverage above which slot k holds its word.
        public float PositionThreshold(int k) => _positionUnit[k];
        public bool HasDigit(int k) => _hasDigit[k];
        public int GlyphBase => _glyphBase;

        // The whitespace before slot `i` (i == Count is the trailing run).
        public string Separator(int i) => _separators[i];

        // Token k's letters and digits as they sit in a slot: authored, or
        // stripped of punctuation and case when its position is loose.
        public string BaseText(int k, bool held) => held ? _original[k] : _loose[k];

        public ScrambleState StateAt(LanguageCoverage coverage)
        {
            var state = new ScrambleState(Count);
            for (int k = 0; k < Count; k++)
            {
                state.held[k] = Known(_positionUnit[k], coverage.grammar);
                state.wordKnown[k] = Known(_wordUnit[k], coverage.vocabulary);
                state.numberKnown[k] = Known(_numberUnit[k], coverage.numbers);
                state.dropped[k] = !state.held[k] && _loose[k].Length == 0;
            }
            RotateLoose(state);
            return state;
        }

        // Sattolo's algorithm over the surviving loose positions only: one cycle
        // through all of them, so no loose word lands back where it started —
        // "not in the right order" is then always true, which is what
        // ComputeComprehension counts. A single loose position has nowhere to
        // go and stays.
        void RotateLoose(ScrambleState state)
        {
            List<int> loose = new List<int>();
            for (int k = 0; k < Count; k++)
            {
                if (state.dropped[k])
                {
                    state.occupant[k] = -1;
                }
                else
                {
                    state.occupant[k] = k;
                    if (!state.held[k])
                    {
                        loose.Add(k);
                    }
                }
            }
            if (loose.Count < 2)
            {
                return;
            }
            int[] words = loose.ToArray();
            Random rng = new Random(_rotateSeed);
            for (int i = words.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i);
                (words[i], words[j]) = (words[j], words[i]);
            }
            for (int i = 0; i < words.Length; i++)
            {
                state.occupant[loose[i]] = words[i];
            }
        }

        // Token k as displayed: its base text with unknown digits and letters
        // substituted. Digits are substituted independently of the word: a known
        // word holding an unknown quantity ("12 stones") reads as the word plus
        // an unreadable number.
        public string TokenText(int k, bool held, bool wordKnown, bool numberKnown)
        {
            string text = BaseText(k, held);
            bool cipherDigits = _hasDigit[k] && !numberKnown;
            bool cipherLetters = _hasLetter[k] && !wordKnown;
            if (!cipherDigits && !cipherLetters)
            {
                return text;
            }
            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                sb.Append(DisplayChar(k, c, wordKnown, numberKnown));
            }
            return sb.ToString();
        }

        // One character of token k as displayed. Without a script, an unknown
        // digit becomes a Latin letter — which the word's cipher then rewrites
        // again when the word is unknown too. Kept: it is what every line has
        // always read as.
        public string DisplayChar(int k, char c, bool wordKnown, bool numberKnown)
        {
            bool cipherLetters = _hasLetter[k] && !wordKnown;
            if (c >= '0' && c <= '9')
            {
                if (!_hasDigit[k] || numberKnown)
                {
                    return c.ToString();
                }
                string digit = CipherDigit(c);
                return cipherLetters && _glyphBase == 0 ? CipherLetter(digit[0]) : digit;
            }
            return cipherLetters ? CipherLetter(c) : c.ToString();
        }

        // One letter through the language's cipher. With a glyphBase it becomes
        // that language's script and the result is unicameral — 'A' and 'a' are
        // one glyph, since capitalization is part of the structure grammar
        // teaches and no invented script here has a second case. Without one the
        // cipher stays in Latin and keeps case.
        //
        // ASCII a-z maps through the permutation and so stays a bijection: a
        // given letter is always the same glyph, which is what lets a player
        // learn the alphabet by eye. Any other script's letters (accented Latin,
        // CJK) have no 26-letter alphabet to permute, so they hash into the same
        // block instead — not a bijection, but nothing decodes this text, and the
        // alternative is leaving them legible in a word the player cannot read.
        // Anything that is not a letter passes through.
        public string CipherLetter(char c)
        {
            if (c >= 'A' && c <= 'Z')
            {
                return Ciphered(_letterPerm[c - 'A'], (char)('A' + _letterPerm[c - 'A']));
            }
            if (c >= 'a' && c <= 'z')
            {
                return Ciphered(_letterPerm[c - 'a'], (char)('a' + _letterPerm[c - 'a']));
            }
            if (_glyphBase > 0 && char.IsLetter(c))
            {
                return GlyphString(_glyphBase + _letterPerm[HashToSlot(c)]);
            }
            return c.ToString();
        }

        // One digit as the language's numeral glyph when it has a script, else
        // a stable letter.
        public string CipherDigit(char c)
        {
            int slot = _digitPerm[c - '0'];
            return _glyphBase > 0 ? GlyphString(_glyphBase + GlyphLetterCount + slot) : ((char)('a' + slot)).ToString();
        }

        string Ciphered(int slot, char latin)
        {
            return _glyphBase > 0 ? GlyphString(_glyphBase + slot) : latin.ToString();
        }

        // The line as `state` reads it.
        public string Flatten(ScrambleState state)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(_separators[0]);
            for (int i = 0; i < Count; i++)
            {
                int k = state.occupant[i];
                if (k >= 0)
                {
                    sb.Append(TokenText(k, state.held[k], state.wordKnown[k], state.numberKnown[k]));
                }
                sb.Append(_separators[i + 1]);
            }
            return sb.ToString();
        }
    }

    // A plan at one coverage. Per TOKEN: whether its position holds, whether
    // its word and number are known, whether it drops out. Per SLOT: which token
    // sits there (-1 = empty).
    public sealed class ScrambleState
    {
        public readonly bool[] held;
        public readonly bool[] wordKnown;
        public readonly bool[] numberKnown;
        public readonly bool[] dropped;
        public readonly int[] occupant;

        public ScrambleState(int count)
        {
            held = new bool[count];
            wordKnown = new bool[count];
            numberKnown = new bool[count];
            dropped = new bool[count];
            occupant = new int[count];
        }

        public bool Dropped(int k) => dropped[k];
    }
}
