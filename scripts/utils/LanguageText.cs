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
// Narration — a stage direction inside a spoken line — is [narrate] ...
// [/narrate]. It is not speech, so it is in no language: never scrambled and
// left out of the comprehension score, so "he points to the tent" neither
// reveals nor dilutes how much of the speech the player followed. RenderBbcode
// draws it in italics; Render shows it plain. Narration and language spans do
// not nest inside each other.
//
//   [narrate]He points to the tent.[/narrate] Vash'eth tolun mira.
//
// A control is [input:<id>] — a ControlBindingData.id from the game's
// ControlSetData, the same rows the controls screen lists — replaced at display
// time by what performs it on the device the player is using right now
// (InputDevice.Current): "LMB+RMB" on keyboard, "RT" on a pad. The per-device
// actions live on the control, so a line names WHAT to do and never which
// action implements it where. It is a single token, not a span — it may sit
// inside a language or narration span, which carries on after it — and like
// narration it is in no language: never scrambled, left out of the
// comprehension score. RenderBbcode draws it bold.
//
//   [input:camera] turns the view.
//
// Every display path goes through Render rather than calling TextScrambler
// directly, because the markup has to be stripped even when nothing is
// scrambled — a fluent player, an unlanguaged sign, or no player at all.
public static class LanguageText
{
    const string TagOpen = "[lang:";
    const string TagClose = "[/lang]";
    const string NarrateOpen = "[narrate]";
    const string NarrateClose = "[/narrate]";
    const string InputOpen = "[input:";
    // Reserved span id: resolves to SimData.commonTongue, which every player
    // knows in full, so nothing in it scrambles.
    const string CommonId = "common";

    readonly struct Span
    {
        public readonly string Text;
        // Null = an unlanguaged line (nothing to scramble). The common tongue
        // is a real LanguageData now, and reads the same way.
        public readonly LanguageData Language;
        // Narration carries no language (Language is null).
        public readonly bool Narration;
        // A resolved [input:] glyph; no language either.
        public readonly bool Input;

        public Span(string text, LanguageData language, bool narration, bool input = false)
        {
            Text = text;
            Language = language;
            Narration = narration;
            Input = input;
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

    // Render for a RichTextLabel with bbcode_enabled: narration wrapped in
    // [i], and every authored '[' escaped so a line can't inject BBCode.
    public static string RenderBbcode(string raw, LanguageData defaultLanguage, Player player)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }
        if (raw.IndexOf('[') < 0)
        {
            return EscapeBbcode(ScrambleFor(raw, defaultLanguage, player));
        }

        List<Span> spans = new List<Span>();
        Parse(raw, defaultLanguage, player, spans);
        StringBuilder sb = new StringBuilder(raw.Length + 16);
        for (int i = 0; i < spans.Count; i++)
        {
            string text = EscapeBbcode(ScrambleFor(spans[i].Text, spans[i].Language, player));
            if (spans[i].Input)
            {
                sb.Append("[b]").Append(text).Append("[/b]");
            }
            else if (spans[i].Narration)
            {
                sb.Append("[i]").Append(text).Append("[/i]");
            }
            else
            {
                sb.Append(text);
            }
        }
        return sb.ToString();
    }

    static string EscapeBbcode(string text)
    {
        return string.IsNullOrEmpty(text) || text.IndexOf('[') < 0 ? text : text.Replace("[", "[lb]");
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
                : TextScrambler.ComputeComprehension(raw, defaultLanguage, player.GetLanguageCoverage(defaultLanguage), grammarWeight);
        }

        List<Span> spans = new List<Span>();
        Parse(raw, defaultLanguage, player, spans);
        float weighted = 0f;
        int totalWeight = 0;
        for (int i = 0; i < spans.Count; i++)
        {
            if (spans[i].Narration || spans[i].Input)
            {
                continue;
            }
            LanguageData lang = spans[i].Language;
            LanguageCoverage coverage = player.GetLanguageCoverage(lang);
            float score = TextScrambler.ComputeComprehension(spans[i].Text, lang, coverage, grammarWeight, out int weight);
            weighted += score * weight;
            totalWeight += weight;
        }
        return totalWeight > 0 ? weighted / totalWeight : 1f;
    }

    // `raw` with every span tag removed and every [input:] token resolved — the
    // line as it reads to someone fluent in everything.
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
                if (Matches(raw, i, InputOpen))
                {
                    int end = raw.IndexOf(']', i + InputOpen.Length);
                    if (end >= 0)
                    {
                        sb.Append(ResolveInput(raw.Substring(i + InputOpen.Length, end - i - InputOpen.Length), raw));
                        i = end + 1;
                        continue;
                    }
                }
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
        if (Matches(raw, index, NarrateOpen))
        {
            return NarrateOpen.Length;
        }
        if (Matches(raw, index, NarrateClose))
        {
            return NarrateClose.Length;
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

    enum ESpanKind { None, Language, Narration }

    // Splits `raw` into runs of one language each, narration its own run.
    // Malformed markup is reported and then treated as the default language
    // rather than dropping the line — the author sees the error with the text
    // still legible.
    static void Parse(string raw, LanguageData defaultLanguage, Player player, List<Span> spans)
    {
        SimData simData = player?.Sim?.SimData;
        LanguageData current = defaultLanguage;
        ESpanKind open = ESpanKind.None;
        int spanStart = 0;
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] != '[')
            {
                i++;
                continue;
            }
            bool langClose = Matches(raw, i, TagClose);
            if (langClose || Matches(raw, i, NarrateClose))
            {
                ESpanKind closing = langClose ? ESpanKind.Language : ESpanKind.Narration;
                string tag = langClose ? TagClose : NarrateClose;
                if (open != closing)
                {
                    GD.PushError($"LanguageText: '{tag}' with no matching open span in \"{raw}\".");
                }
                AddSpan(spans, raw, spanStart, i, current, open == ESpanKind.Narration);
                current = defaultLanguage;
                open = ESpanKind.None;
                i += tag.Length;
                spanStart = i;
                continue;
            }
            if (Matches(raw, i, NarrateOpen))
            {
                if (open != ESpanKind.None)
                {
                    GD.PushError($"LanguageText: '{NarrateOpen}' inside another span in \"{raw}\" — spans don't nest.");
                }
                AddSpan(spans, raw, spanStart, i, current, open == ESpanKind.Narration);
                current = null;
                open = ESpanKind.Narration;
                i += NarrateOpen.Length;
                spanStart = i;
                continue;
            }
            if (Matches(raw, i, InputOpen))
            {
                int end = raw.IndexOf(']', i + InputOpen.Length);
                if (end < 0)
                {
                    GD.PushError($"LanguageText: unterminated '{InputOpen}' tag in \"{raw}\".");
                    break;
                }
                // A token, not a span: the run around it keeps its language and
                // resumes after it.
                AddSpan(spans, raw, spanStart, i, current, open == ESpanKind.Narration);
                string body = raw.Substring(i + InputOpen.Length, end - i - InputOpen.Length);
                spans.Add(new Span(ResolveInput(body, raw), null, false, true));
                i = end + 1;
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
                if (open != ESpanKind.None)
                {
                    GD.PushError($"LanguageText: '{TagOpen}' inside another span in \"{raw}\" — spans don't nest.");
                }
                AddSpan(spans, raw, spanStart, i, current, open == ESpanKind.Narration);
                string id = raw.Substring(i + TagOpen.Length, end - i - TagOpen.Length);
                current = Resolve(id, defaultLanguage, simData, raw);
                open = ESpanKind.Language;
                i = end + 1;
                spanStart = i;
                continue;
            }
            i++;
        }
        if (open != ESpanKind.None)
        {
            string tag = open == ESpanKind.Narration ? NarrateClose : TagClose;
            GD.PushError($"LanguageText: span left open (missing '{tag}') in \"{raw}\".");
        }
        AddSpan(spans, raw, spanStart, raw.Length, current, open == ESpanKind.Narration);
    }

    static void AddSpan(List<Span> spans, string raw, int start, int end, LanguageData language, bool narration)
    {
        if (end > start)
        {
            spans.Add(new Span(raw.Substring(start, end - start), language, narration));
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
        if (simData == null)
        {
            // No player to scramble for, so the language is never read.
            return defaultLanguage;
        }
        LanguageData found = simData?.LanguageById(id);
        if (found == null)
        {
            GD.PushError($"LanguageText: unknown language id '{id}' in \"{raw}\" — add a LanguageData with that id to SimData.languages, or use '{CommonId}'.");
            return defaultLanguage;
        }
        return found;
    }

    // What performs the control named `id` on the device in use now. A missing
    // control or one unbound on this device shows the controls screen's
    // "unbound" text, so the line stays readable while the author hears about it.
    static string ResolveInput(string id, string raw)
    {
        ControlSetData controls = ControlSetData.Current;
        InputDevice.EDevice device = InputDevice.Current;
        ControlBindingData binding = controls?.Find(id);
        if (binding == null)
        {
            GD.PushError($"LanguageText: [input:{id}] names no control in the ControlSetData in \"{raw}\".");
        }
        else if (controls.TryCompose(binding, device, out string glyphs))
        {
            return glyphs;
        }
        else
        {
            GD.PushError($"LanguageText: [input:{id}] has nothing bound on {device} in \"{raw}\".");
        }
        return Loc.Get(Loc.Keys.controls_unbound);
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
        return TextScrambler.Scramble(text, language, player.GetLanguageCoverage(language));
    }
}
