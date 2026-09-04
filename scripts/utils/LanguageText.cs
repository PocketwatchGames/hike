using System.Collections.Generic;
using System.Text;
using Godot;

// Renders a line of authored text that may switch language mid-sentence,
// and scores how much of it the player understands.
//
// Authoring: a span is wrapped in [lang:<id>] ... [/lang], where <id> is a
// LanguageData.id registered on SimData.languages, or the reserved id
// "common", which resolves to SimData.commonTongue — the tongue every
// character is born speaking, so nothing in it ever scrambles. Text outside
// any span is in the line's default language: the speaker's tongue, or the
// language an inscription is written in.
//
//   Please, find the [lang:common]Sanctuary[/lang]. For them.
//   I traded with the [lang:muddish]gravewalkers[/lang] near the coast.
//
// Every span is scrambled against ITS OWN language and the player's
// knowledge of that one, so a Muddish word quoted inside a Vyeshal line
// stays unreadable to a fluent Vyeshal speaker. Spans do not nest.
//
// Grammar's word shuffle runs per span, so a recognized word stays where it
// was spoken and the fragments around it jumble within themselves — which is
// the point: one anchor the player understood in a line they didn't.
//
// Every display path goes through Render rather than calling TextScrambler
// directly, because the markup has to be stripped even when nothing is
// scrambled — a fluent player, an unlanguaged sign, or no player at all.
public static class LanguageText
{
    const string TagOpen = "[lang:";
    const string TagClose = "[/lang]";
    // Reserved span id: resolves to SimData.commonTongue, which every player
    // knows in full, so nothing in it scrambles.
    const string CommonId = "common";

    readonly struct Span
    {
        public readonly string Text;
        // Null = an unlanguaged line (nothing to scramble). The common tongue
        // is a real LanguageData now, and reads the same way.
        public readonly LanguageData Language;

        public Span(string text, LanguageData language)
        {
            Text = text;
            Language = language;
        }
    }

    // Display form of `raw` for `player`: markup removed, each span
    // scrambled by the player's knowledge of that span's language.
    // `defaultLanguage` is the language of everything outside a span.
    public static string Render(string raw, LanguageData defaultLanguage, Player player)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }
        if (raw.IndexOf('[') < 0)
        {
            return ScrambleFor(raw, defaultLanguage, player);
        }
        if (player == null)
        {
            return Strip(raw);
        }

        List<Span> spans = new List<Span>();
        Parse(raw, defaultLanguage, player, spans);
        StringBuilder sb = new StringBuilder(raw.Length);
        for (int i = 0; i < spans.Count; i++)
        {
            sb.Append(ScrambleFor(spans[i].Text, spans[i].Language, player));
        }
        return sb.ToString();
    }

    // Fraction of `raw` the player understands, in [0, 1] — per-span
    // ComputeComprehension weighted by how many word tokens each span is.
    // Drives ConversationVisibility's soft gate on response options.
    public static float Comprehension(string raw, LanguageData defaultLanguage, Player player, float grammarWeight)
    {
        if (string.IsNullOrEmpty(raw) || player == null)
        {
            return 1f;
        }
        if (raw.IndexOf('[') < 0)
        {
            return defaultLanguage == null
                ? 1f
                : TextScrambler.ComputeComprehension(raw, defaultLanguage, player.GetLearnedComponents(defaultLanguage), grammarWeight);
        }

        List<Span> spans = new List<Span>();
        Parse(raw, defaultLanguage, player, spans);
        float weighted = 0f;
        int totalWeight = 0;
        for (int i = 0; i < spans.Count; i++)
        {
            LanguageData lang = spans[i].Language;
            ELanguageComponents learned = lang == null
                ? ELanguageComponents.All
                : player.GetLearnedComponents(lang);
            float score = TextScrambler.ComputeComprehension(spans[i].Text, lang, learned, grammarWeight, out int weight);
            weighted += score * weight;
            totalWeight += weight;
        }
        return totalWeight > 0 ? weighted / totalWeight : 1f;
    }

    // `raw` with every span tag removed and nothing else changed — the line
    // as it reads to someone fluent in everything.
    public static string Strip(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.IndexOf('[') < 0)
        {
            return raw;
        }
        StringBuilder sb = new StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '[')
            {
                int skip = TagLengthAt(raw, i);
                if (skip > 0)
                {
                    i += skip;
                    continue;
                }
            }
            sb.Append(raw[i]);
            i++;
        }
        return sb.ToString();
    }

    // Length of the span tag starting at `index`, or 0 if there isn't one.
    static int TagLengthAt(string raw, int index)
    {
        if (Matches(raw, index, TagClose))
        {
            return TagClose.Length;
        }
        if (Matches(raw, index, TagOpen))
        {
            int end = raw.IndexOf(']', index + TagOpen.Length);
            if (end >= 0)
            {
                return end - index + 1;
            }
        }
        return 0;
    }

    static bool Matches(string raw, int index, string tag)
    {
        return index + tag.Length <= raw.Length
            && string.CompareOrdinal(raw, index, tag, 0, tag.Length) == 0;
    }

    // Splits `raw` into runs of one language each. Malformed markup is
    // reported and then treated as the default language rather than dropping
    // the line — the author sees the error with the text still legible.
    static void Parse(string raw, LanguageData defaultLanguage, Player player, List<Span> spans)
    {
        SimData simData = player?.Sim?.SimData;
        LanguageData current = defaultLanguage;
        bool inSpan = false;
        int spanStart = 0;
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] != '[')
            {
                i++;
                continue;
            }
            if (Matches(raw, i, TagClose))
            {
                if (!inSpan)
                {
                    GD.PushError($"LanguageText: '{TagClose}' with no open span in \"{raw}\".");
                }
                AddSpan(spans, raw, spanStart, i, current);
                current = defaultLanguage;
                inSpan = false;
                i += TagClose.Length;
                spanStart = i;
                continue;
            }
            if (Matches(raw, i, TagOpen))
            {
                int end = raw.IndexOf(']', i + TagOpen.Length);
                if (end < 0)
                {
                    GD.PushError($"LanguageText: unterminated '{TagOpen}' tag in \"{raw}\".");
                    break;
                }
                if (inSpan)
                {
                    GD.PushError($"LanguageText: nested '{TagOpen}' tag in \"{raw}\" — spans don't nest.");
                }
                AddSpan(spans, raw, spanStart, i, current);
                string id = raw.Substring(i + TagOpen.Length, end - i - TagOpen.Length);
                current = Resolve(id, defaultLanguage, simData, raw);
                inSpan = true;
                i = end + 1;
                spanStart = i;
                continue;
            }
            i++;
        }
        if (inSpan)
        {
            GD.PushError($"LanguageText: span left open (missing '{TagClose}') in \"{raw}\".");
        }
        AddSpan(spans, raw, spanStart, raw.Length, current);
    }

    static void AddSpan(List<Span> spans, string raw, int start, int end, LanguageData language)
    {
        if (end > start)
        {
            spans.Add(new Span(raw.Substring(start, end - start), language));
        }
    }

    static LanguageData Resolve(string id, LanguageData defaultLanguage, SimData simData, string raw)
    {
        if (id == CommonId)
        {
            // SimData.commonTongue when authored; null otherwise, which behaves
            // identically (both render unscrambled and score 1) so a world
            // missing the reference still reads correctly.
            return simData?.commonTongue;
        }
        LanguageData found = simData?.LanguageById(id);
        if (found == null)
        {
            GD.PushError($"LanguageText: unknown language id '{id}' in \"{raw}\" — add a LanguageData with that id to SimData.languages, or use '{CommonId}'.");
            return defaultLanguage;
        }
        return found;
    }

    // One span's display form. No language, no player, or full fluency all
    // mean "as authored"; this is the shape every call site used to spell out
    // for itself before spans existed.
    static string ScrambleFor(string text, LanguageData language, Player player)
    {
        if (language == null || player == null || string.IsNullOrEmpty(text))
        {
            return text;
        }
        ELanguageComponents missing = ELanguageComponents.All & ~player.GetLearnedComponents(language);
        return missing == ELanguageComponents.None ? text : TextScrambler.Scramble(text, language, missing);
    }
}
