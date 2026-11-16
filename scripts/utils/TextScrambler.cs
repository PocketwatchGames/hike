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
// LanguageData.glyphBase — see ApplyLetterCipher. Nothing downstream knows:
// the codepoints land in a private-use block that the UI font reaches through
// its `fallbacks`, so a half-understood line renders Latin and alien glyphs
// side by side in one plain Label.
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
        int seed = StableSeed(language.displayName.ToString());
        int[] letterPerm = BuildPermutation(seed, GlyphLetterCount);
        // Digit substitution uses an independent permutation so it isn't
        // trivially derivable from the letter cipher.
        int[] digitPerm = BuildPermutation(seed ^ 0x55AA55AA, GlyphNumeralCount);
        int glyphBase = language.glyphBase;

        List<string> tokens = new List<string>();
        List<string> separators = new List<string>();
        Tokenize(text, tokens, separators);

        // Grammar first: it decides which tokens survive at all, and the
        // per-word and per-number tests below read the cleaned tokens.
        List<bool> held = HoldPositions(tokens, seed, coverage.grammar);
        StripLoose(tokens, separators, held);

        for (int i = 0; i < tokens.Count; i++)
        {
            string tok = tokens[i];
            if (HasDigit(tok) && !IsNumberKnown(tok, seed, coverage.numbers))
            {
                tok = SubstituteDigits(tok, digitPerm, glyphBase);
            }
            // The cipher reads letters only, so a word hashed before the digit
            // substitution above is still the same word.
            if (HasLetter(tokens[i]) && !IsWordKnown(tokens[i], seed, coverage.vocabulary))
            {
                tok = ApplyLetterCipher(tok, letterPerm, glyphBase);
            }
            tokens[i] = tok;
        }

        RotateLoose(tokens, held, seed ^ unchecked((int)0xDEADBEEF));
        return Reassemble(tokens, separators);
    }

    // Salts keep the three per-aspect hashes independent of one another, so
    // knowing a word says nothing about whether its position holds.
    const uint WORD_SALT = 0x9E3779B9u;
    const uint NUMBER_SALT = 0x85EBCA6Bu;
    const uint POSITION_SALT = 0xC2B2AE35u;

    static bool IsWordKnown(string token, int seed, float coverage)
    {
        if (coverage >= 1f)
        {
            return true;
        }
        uint h = (uint)seed ^ WORD_SALT;
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (!char.IsLetter(c)) { continue; }
            h = unchecked(h * 31u + char.ToLowerInvariant(c));
        }
        return Unit(h) < coverage;
    }

    static bool IsNumberKnown(string token, int seed, float coverage)
    {
        if (coverage >= 1f)
        {
            return true;
        }
        uint h = (uint)seed ^ NUMBER_SALT;
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (c < '0' || c > '9') { continue; }
            h = unchecked(h * 31u + c);
        }
        return Unit(h) < coverage;
    }

    static bool IsPositionHeld(int index, int seed, float coverage)
    {
        if (coverage >= 1f)
        {
            return true;
        }
        uint h = unchecked((uint)seed ^ POSITION_SALT ^ ((uint)index * 0x27D4EB2Fu));
        return Unit(h) < coverage;
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

    // Which ORIGINAL token positions hold their word, decided before anything
    // is dropped so a position's fate doesn't depend on its neighbours.
    static List<bool> HoldPositions(List<string> tokens, int seed, float grammar)
    {
        List<bool> held = new List<bool>(tokens.Count);
        for (int i = 0; i < tokens.Count; i++)
        {
            held.Add(IsPositionHeld(i, seed, grammar));
        }
        return held;
    }

    // Walk the string splitting into alternating whitespace runs and
    // non-whitespace word tokens. Always emits separators.Count == tokens.Count + 1
    // (leading/trailing entries may be empty) so a Reassemble pass can
    // interleave them back into the original shape.
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

    static string Reassemble(List<string> tokens, List<string> separators)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(separators[0]);
        for (int i = 0; i < tokens.Count; i++)
        {
            sb.Append(tokens[i]);
            sb.Append(separators[i + 1]);
        }
        return sb.ToString();
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

        List<string> tokens = new List<string>();
        List<string> separators = new List<string>();
        Tokenize(text, tokens, separators);
        weight = tokens.Count;
        if (language == null || tokens.Count == 0 || coverage.IsFull)
        {
            return 1f;
        }

        int seed = StableSeed(language.displayName.ToString());
        List<bool> held = HoldPositions(tokens, seed, coverage.grammar);
        StripLoose(tokens, separators, held);
        weight = tokens.Count;
        if (tokens.Count == 0)
        {
            return 1f;
        }

        int translated = 0;
        int heldCount = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            string tok = tokens[i];
            bool letterOK = !HasLetter(tok) || IsWordKnown(tok, seed, coverage.vocabulary);
            bool digitOK = !HasDigit(tok) || IsNumberKnown(tok, seed, coverage.numbers);
            if (letterOK && digitOK)
            {
                translated++;
            }
            if (held[i])
            {
                heldCount++;
            }
        }
        int inPlace = tokens.Count - heldCount == 1 ? heldCount + 1 : heldCount;
        float translatedPct = (float)translated / tokens.Count;
        float orderPct = (float)inPlace / tokens.Count;
        return translatedPct * ((1f - grammarWeight) + orderPct * grammarWeight);
    }

    // Any script's letters count, not just ASCII — an accented or CJK word is
    // still a word the vocabulary cipher has to be able to hide.
    static bool HasLetter(string token)
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

    static bool HasDigit(string token)
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

    // Digits become the language's own numeral glyphs when it has a script,
    // else a stable letter substitution. Deliberately independent of whether the
    // word is known: a known word holding an unknown quantity
    // ("12 stones") should read as the word plus an unreadable number.
    static string SubstituteDigits(string token, int[] digitPerm, int glyphBase)
    {
        // Most tokens have no digits; skip the StringBuilder allocation in
        // that case so non-numeric paragraphs are essentially free.
        bool any = false;
        for (int i = 0; i < token.Length; i++)
        {
            if (token[i] >= '0' && token[i] <= '9') { any = true; break; }
        }
        if (!any) { return token; }
        StringBuilder sb = new StringBuilder(token.Length);
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (c >= '0' && c <= '9')
            {
                int slot = digitPerm[c - '0'];
                if (glyphBase > 0)
                {
                    AppendGlyph(sb, glyphBase + GlyphLetterCount + slot);
                }
                else
                {
                    sb.Append((char)('a' + slot));
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // With a glyphBase the token is rewritten into that language's script and
    // the result is unicameral — 'A' and 'a' are one glyph, since capitalization
    // is part of the structure grammar teaches and no invented script here
    // has a second case. Without one the cipher stays in Latin and keeps case.
    //
    // ASCII a-z maps through `perm` and so stays a bijection: a given letter is
    // always the same glyph, which is what lets a player learn the alphabet by
    // eye. Any other script's letters (accented Latin, CJK) have no 26-letter
    // alphabet to permute, so they hash into the same block instead — not a
    // bijection, but nothing decodes this text, and the alternative is leaving
    // them legible in the middle of a word the player cannot read.
    static string ApplyLetterCipher(string token, int[] perm, int glyphBase)
    {
        StringBuilder sb = new StringBuilder(token.Length);
        foreach (char c in token)
        {
            if (c >= 'A' && c <= 'Z')
            {
                AppendCiphered(sb, perm[c - 'A'], glyphBase, (char)('A' + perm[c - 'A']));
            }
            else if (c >= 'a' && c <= 'z')
            {
                AppendCiphered(sb, perm[c - 'a'], glyphBase, (char)('a' + perm[c - 'a']));
            }
            else if (glyphBase > 0 && char.IsLetter(c))
            {
                AppendGlyph(sb, glyphBase + perm[HashToSlot(c)]);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    static void AppendCiphered(StringBuilder sb, int slot, int glyphBase, char latin)
    {
        if (glyphBase > 0)
        {
            AppendGlyph(sb, glyphBase + slot);
        }
        else
        {
            sb.Append(latin);
        }
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
    static void AppendGlyph(StringBuilder sb, int codepoint)
    {
        if (codepoint <= char.MaxValue)
        {
            sb.Append((char)codepoint);
        }
        else
        {
            sb.Append(char.ConvertFromUtf32(codepoint));
        }
    }

    // Sattolo's algorithm over the loose positions only: one cycle through
    // all of them, so no loose word lands back where it started — "not in the
    // right order" is then always true, which is what ComputeComprehension
    // counts. A single loose position has nowhere to go and stays.
    static void RotateLoose(List<string> tokens, List<bool> held, int seed)
    {
        List<int> loose = new List<int>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!held[i])
            {
                loose.Add(i);
            }
        }
        if (loose.Count < 2)
        {
            return;
        }
        string[] words = new string[loose.Count];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = tokens[loose[i]];
        }
        Random rng = new Random(seed);
        for (int i = words.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i);
            (words[i], words[j]) = (words[j], words[i]);
        }
        for (int i = 0; i < words.Length; i++)
        {
            tokens[loose[i]] = words[i];
        }
    }

    // A loose token loses its punctuation and case — those belong to the
    // structure grammar teaches ("don't" → "dont", "Hello," → "hello",
    // "1,000" → "1000"); one that was nothing but punctuation ("—", "...")
    // drops out, its separators merged so reassembly stays aligned
    // (separators.Count == tokens.Count + 1) and line breaks survive. Held
    // tokens are left exactly as authored. `held` is kept parallel to `tokens`.
    static void StripLoose(List<string> tokens, List<string> separators, List<bool> held)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            if (held[i])
            {
                continue;
            }
            string tok = tokens[i];
            sb.Clear();
            for (int j = 0; j < tok.Length; j++)
            {
                char c = tok[j];
                // Letter-or-digit in ANY script, not just ASCII: the test here
                // decides what survives, so an ASCII-only one silently deletes
                // every accented or CJK character in a localized line.
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            if (sb.Length == 0)
            {
                separators[i + 1] = separators[i] + separators[i + 1];
                tokens.RemoveAt(i);
                separators.RemoveAt(i);
                held.RemoveAt(i);
            }
            else
            {
                tokens[i] = sb.ToString();
            }
        }
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
}
