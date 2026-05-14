using System;
using System.Text;

// Renders text the player can't read as a stable per-language substitution
// cipher. Mobs and signposts call this at display time when the player has
// not learned the source LanguageData — the underlying string data stays
// intact, only the rendered glyphs change. Same input + same language
// always produces the same gibberish so a re-read of the same signpost
// looks identical.
public static class TextScrambler
{
    // Scrambles letters A-Z / a-z via a deterministic permutation seeded by
    // `language`. Whitespace, digits, and punctuation pass through so word
    // shapes and line breaks survive. Null language (or empty text) returns
    // `text` unchanged.
    public static string Scramble(string text, LanguageData language)
    {
        if (language == null || string.IsNullOrEmpty(text))
        {
            return text;
        }
        int[] permutation = BuildPermutation(language);
        StringBuilder sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c >= 'A' && c <= 'Z')
            {
                sb.Append((char)('A' + permutation[c - 'A']));
            }
            else if (c >= 'a' && c <= 'z')
            {
                sb.Append((char)('a' + permutation[c - 'a']));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    static int[] BuildPermutation(LanguageData language)
    {
        int seed = StableSeed(language.displayName.ToString());
        Random rng = new Random(seed);
        int[] p = new int[26];
        for (int i = 0; i < 26; i++)
        {
            p[i] = i;
        }
        // Fisher-Yates. The shuffle may leave a small number of letters
        // mapped to themselves; for a 26-element set the expected count is
        // ~1, which is acceptable noise inside otherwise scrambled words.
        for (int i = 25; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        return p;
    }

    // Stable across runs and platforms (unlike string.GetHashCode, which
    // .NET randomizes per process). Keeps a given language's gibberish
    // visually consistent across save/reload.
    static int StableSeed(string s)
    {
        int h = 0;
        for (int i = 0; i < s.Length; i++)
        {
            h = unchecked(h * 31 + s[i]);
        }
        return h;
    }
}
