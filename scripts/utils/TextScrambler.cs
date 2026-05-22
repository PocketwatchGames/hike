using System;
using System.Collections.Generic;
using System.Text;

// Renders text the player can only partially read by applying one
// scrambling transform per missing language component. Mobs and signposts
// call this at display time with the components the player has NOT yet
// learned; the underlying string data stays intact. Same input + same
// language + same missing-set always produces the same gibberish so
// re-reading the same line looks identical.
public static class TextScrambler
{
    // Applies the transforms named by `missing` to `text`. Per component:
    //   Vocabulary1-3 — each word with letters is bucketed into one of
    //                   three vocabulary slots by a stable hash of its
    //                   letters. The bucket's Vocabulary_N flag, when
    //                   missing, runs that word's letters through the
    //                   per-language substitution cipher; learning the
    //                   flag reveals those words in their original glyphs.
    //                   Roughly a third of any text resolves per learned
    //                   vocabulary component.
    //   Numbers       — replaces every digit anywhere in the text with a
    //                   stable per-language letter.
    //   Grammar       — permutes whitespace-bounded word tokens WITHIN each
    //                   sentence (sentences are bounded by tokens ending in
    //                   . ! ?); whitespace runs between tokens stay in
    //                   place so line breaks survive, and tokens never
    //                   cross a sentence boundary.
    // Null language, empty text, or missing == None returns `text` unchanged.
    public static string Scramble(string text, LanguageData language, ELanguageComponents missing)
    {
        if (language == null || missing == ELanguageComponents.None || string.IsNullOrEmpty(text))
        {
            return text;
        }
        int seed = StableSeed(language.displayName.ToString());
        int[] letterPerm = BuildPermutation(seed, 26);
        // Digit→letter substitution uses an independent permutation so it
        // isn't trivially derivable from the letter cipher.
        int[] digitPerm = BuildPermutation(seed ^ 0x55AA55AA, 26);

        bool doNumbers = (missing & ELanguageComponents.Numbers) != 0;
        bool doGrammar = (missing & ELanguageComponents.Grammar) != 0;
        ELanguageComponents missingVocab = missing & (ELanguageComponents.Vocabulary1 | ELanguageComponents.Vocabulary2 | ELanguageComponents.Vocabulary3);

        List<string> tokens = new List<string>();
        List<string> separators = new List<string>();
        Tokenize(text, tokens, separators);

        for (int i = 0; i < tokens.Count; i++)
        {
            string tok = tokens[i];
            bool hasLetter = HasLetter(tok);
            if (doNumbers)
            {
                tok = SubstituteDigits(tok, digitPerm);
            }
            // Vocabulary cipher: applied only when this word's bucket is
            // among the player's missing components. Each bucket's reveal
            // is fully word-local — different buckets share one alphabet
            // permutation per language so the resulting gibberish reads as
            // a single consistent script.
            if (hasLetter && missingVocab != ELanguageComponents.None
                && (missingVocab & VocabularyBucketFor(tok)) != 0)
            {
                tok = ApplyLetterCipher(tok, letterPerm);
            }
            tokens[i] = tok;
        }

        if (doGrammar && tokens.Count > 1)
        {
            ShuffleSentences(tokens, seed ^ unchecked((int)0xDEADBEEF));
        }

        return Reassemble(tokens, separators);
    }

    // Returns the single Vocabulary_N flag the word falls into. Computed
    // from a case-insensitive, punctuation-stripped hash of the letters
    // only so "Hello,", "hello", and "HELLO!" all share a bucket — the
    // vocabulary mechanic treats them as the same word.
    static ELanguageComponents VocabularyBucketFor(string token)
    {
        int h = 0;
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (c >= 'A' && c <= 'Z') { c = (char)(c - 'A' + 'a'); }
            else if (c < 'a' || c > 'z') { continue; }
            h = unchecked(h * 31 + c);
        }
        int bucket = (h & 0x7FFFFFFF) % 3;
        return bucket switch
        {
            0 => ELanguageComponents.Vocabulary1,
            1 => ELanguageComponents.Vocabulary2,
            _ => ELanguageComponents.Vocabulary3,
        };
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

    // Player's comprehension of `text` in `language` as a value in [0, 1],
    // averaging two axes Scramble uses:
    //   translatedPct — fraction of tokens whose letters AND digits all
    //                   resolve under the player's learned components (the
    //                   token's vocab bucket is learned + Numbers learned if
    //                   the token contains digits).
    //   orderPct      — 1 if Grammar is learned (or there's only one token);
    //                   otherwise the fraction of tokens that happen to land
    //                   at their original index after the same per-sentence
    //                   Fisher-Yates shuffle Scramble would apply. Single-
    //                   token sentences are always fixed points.
    // Used as the soft gate on ConversationResponse visibility (see
    // ConversationVisibility) — a response with a stable per-key RNG roll
    // below this value renders, others stay hidden until the player learns
    // enough to push comprehension past their threshold.
    //
    // `grammarWeight` controls how much the orderPct axis contributes:
    // final = translatedPct × ((1 - grammarWeight) + orderPct × grammarWeight).
    // Lives on SimData.LanguageGrammarWeight so designers can retune
    // without touching code.
    public static float ComputeComprehension(string text, LanguageData language, ELanguageComponents learned, float grammarWeight)
    {
        if (string.IsNullOrEmpty(text) || language == null)
        {
            return 1f;
        }
        if ((learned & ELanguageComponents.All) == ELanguageComponents.All)
        {
            return 1f;
        }

        List<string> tokens = new List<string>();
        List<string> separators = new List<string>();
        Tokenize(text, tokens, separators);
        if (tokens.Count == 0)
        {
            return 1f;
        }

        ELanguageComponents missing = ELanguageComponents.All & ~learned;
        ELanguageComponents missingVocab = missing & (ELanguageComponents.Vocabulary1 | ELanguageComponents.Vocabulary2 | ELanguageComponents.Vocabulary3);
        bool numbersKnown = (missing & ELanguageComponents.Numbers) == 0;
        bool grammarKnown = (missing & ELanguageComponents.Grammar) == 0;

        int translated = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            string tok = tokens[i];
            bool hasLetter = HasLetter(tok);
            bool hasDigit = HasDigit(tok);
            bool letterOK = !hasLetter || (missingVocab & VocabularyBucketFor(tok)) == 0;
            bool digitOK = !hasDigit || numbersKnown;
            if (letterOK && digitOK)
            {
                translated++;
            }
        }
        float translatedPct = (float)translated / tokens.Count;

        float orderPct;
        if (grammarKnown || tokens.Count <= 1)
        {
            orderPct = 1f;
        }
        else
        {
            // Replay the same per-sentence shuffle Scramble would apply,
            // tracking an index permutation. Counting fixed points across
            // every sentence's range gives the exact fraction of words that
            // would land at their original position.
            int shuffleSeed = StableSeed(language.displayName.ToString()) ^ unchecked((int)0xDEADBEEF);
            int[] perm = new int[tokens.Count];
            for (int i = 0; i < perm.Length; i++)
            {
                perm[i] = i;
            }
            Random rng = new Random(shuffleSeed);
            int sStart = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!EndsSentence(tokens[i]) && i != tokens.Count - 1)
                {
                    continue;
                }
                // Fisher-Yates over the sentence range [sStart, i].
                for (int k = i; k > sStart; k--)
                {
                    int j = sStart + rng.Next(k - sStart + 1);
                    (perm[k], perm[j]) = (perm[j], perm[k]);
                }
                sStart = i + 1;
            }
            int fixedPoints = 0;
            for (int i = 0; i < perm.Length; i++)
            {
                if (perm[i] == i)
                {
                    fixedPoints++;
                }
            }
            orderPct = (float)fixedPoints / tokens.Count;
        }

        return translatedPct * ((1f - grammarWeight) + orderPct * grammarWeight);
    }

    static bool HasLetter(string token)
    {
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
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

    static string SubstituteDigits(string token, int[] digitPerm)
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
                sb.Append((char)('a' + digitPerm[c - '0']));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    static string ApplyLetterCipher(string token, int[] perm)
    {
        StringBuilder sb = new StringBuilder(token.Length);
        foreach (char c in token)
        {
            if (c >= 'A' && c <= 'Z')
            {
                sb.Append((char)('A' + perm[c - 'A']));
            }
            else if (c >= 'a' && c <= 'z')
            {
                sb.Append((char)('a' + perm[c - 'a']));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // Per-sentence Fisher-Yates: walks `tokens`, finding ranges bounded by
    // tokens whose trailing punctuation (after stripping wrapping quotes /
    // brackets) is . ! or ?, and shuffles each range in place. A single
    // RNG threads through every sentence so the same seed always produces
    // the same overall permutation. Single-token sentences pass through
    // unshuffled — there's nothing to permute.
    static void ShuffleSentences(List<string> tokens, int seed)
    {
        Random rng = new Random(seed);
        int start = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!EndsSentence(tokens[i]) && i != tokens.Count - 1)
            {
                continue;
            }
            for (int k = i; k > start; k--)
            {
                int j = start + rng.Next(k - start + 1);
                (tokens[k], tokens[j]) = (tokens[j], tokens[k]);
            }
            start = i + 1;
        }
    }

    // Token ends a sentence iff its last character (after stripping trailing
    // quotes / closing brackets) is one of the terminator marks. Permissive
    // about wrappers so "world." and "world.'" and "world.)" all count.
    static bool EndsSentence(string token)
    {
        int i = token.Length - 1;
        while (i >= 0)
        {
            char c = token[i];
            if (c == '"' || c == '\'' || c == ')' || c == ']' || c == '}')
            {
                i--;
                continue;
            }
            break;
        }
        if (i < 0)
        {
            return false;
        }
        char last = token[i];
        return last == '.' || last == '!' || last == '?';
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
