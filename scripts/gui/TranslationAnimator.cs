using System;
using System.Collections.Generic;
using System.Text;
using Godot;

// Plays a line of authored text translating itself as the reader's grasp of
// one language rises from `from` to `to` (amounts learned, 0..1). Pure C#: the
// BookPage steps it each frame and draws Bbcode().
//
// The meter IS the clock, and the text resolves exactly when it is full:
//   - Every word and number is revealed by TextScrambler at the moment its hash
//     drops under the coverage, so sampling the coverage at the meter's value
//     each frame says which just crossed. Each of its characters then lands on
//     the real one at its own random moment, flickering through the script
//     just before — within the reveal window, shortened so the last letter
//     lands by the time the meter fills.
//   - The word order is planned up front: every move from the arrangement at
//     `from` to the one at `to`, positions that become held first (in the order
//     their grammar threshold is crossed), then the leftover loose words. The
//     moves play one at a time — a crossfaded swap — spaced evenly across the
//     fill. A word gets its punctuation and capitals back once it is home in a
//     position that holds.
// The last frame is exactly what LanguageText renders at `to`; the animator
// checks that and reports a mismatch.
//
// Only runs in the taught language animate. Every other run (narration,
// controls, another tongue) is drawn statically at the reader's current
// knowledge.
public sealed class TranslationAnimator
{
    public struct Tuning
    {
        public float holdBeforeSeconds;
        public float fillSeconds;
        public float revealWindowSeconds;
        public float flickerSeconds;
        public float flickerIntervalSeconds;

        public float FillStart => holdBeforeSeconds;
        public float FillEnd => holdBeforeSeconds + fillSeconds;
    }

    readonly List<object> _parts = new();
    readonly LanguageData _language;
    readonly float _from;
    readonly float _to;
    readonly Tuning _tuning;
    float _time;
    bool _verified;

    public float Learned { get; private set; }
    public bool IsDone { get; private set; }
    // False when the last frame disagreed with TextScrambler (also reported).
    public bool FinalMatches { get; private set; } = true;

    public TranslationAnimator(string raw, LanguageData defaultLanguage, LanguageData taught, Player player,
        float from, float to, Tuning tuning)
    {
        _language = taught;
        _from = Mathf.Clamp(from, 0f, 1f);
        _to = Mathf.Clamp(Mathf.Max(to, from), 0f, 1f);
        _tuning = tuning;
        Learned = _from;
        LanguageCoverage start = Coverage(_from);
        LanguageCoverage end = Coverage(_to);
        foreach (LanguageText.Span span in LanguageText.Spans(raw, defaultLanguage, player))
        {
            if (taught != null && span.Language == taught && !span.Narration && !span.Input)
            {
                _parts.Add(new AnimatedSpan(span, start, end, tuning));
            }
            else
            {
                _parts.Add(LanguageText.SpanBbcode(span, player));
            }
        }
        if (_from >= _to || tuning.fillSeconds <= 0f)
        {
            Finish();
        }
    }

    LanguageCoverage Coverage(float learned)
    {
        return _language == null ? LanguageCoverage.Full : _language.SampleCoverage(learned);
    }

    // Steps the animation; returns false once nothing more will change.
    public bool Advance(float delta)
    {
        if (IsDone)
        {
            return false;
        }
        _time += delta;
        float fillT = Mathf.Clamp((_time - _tuning.FillStart) / _tuning.fillSeconds, 0f, 1f);
        Learned = Mathf.Lerp(_from, _to, Mathf.SmoothStep(0f, 1f, fillT));
        LanguageCoverage coverage = Coverage(Learned);
        foreach (object part in _parts)
        {
            (part as AnimatedSpan)?.Step(_time, coverage);
        }
        if (fillT >= 1f)
        {
            IsDone = true;
            Verify();
        }
        return true;
    }

    // Jumps straight to the final frame (the reader pressed to skip).
    public void Finish()
    {
        LanguageCoverage end = Coverage(_to);
        foreach (object part in _parts)
        {
            (part as AnimatedSpan)?.Finish(end);
        }
        _time = Mathf.Max(_time, _tuning.FillEnd);
        Learned = _to;
        IsDone = true;
        Verify();
    }

    public string Bbcode(Color textColor)
    {
        StringBuilder sb = new StringBuilder();
        foreach (object part in _parts)
        {
            if (part is AnimatedSpan anim)
            {
                sb.Append(anim.Bbcode(_time, textColor));
            }
            else
            {
                sb.Append((string)part);
            }
        }
        return sb.ToString();
    }

    void Verify()
    {
        if (_verified)
        {
            return;
        }
        _verified = true;
        LanguageCoverage end = Coverage(_to);
        foreach (object part in _parts)
        {
            if (part is AnimatedSpan anim && !anim.MatchesFinal(_time, end))
            {
                FinalMatches = false;
                GD.PushError($"TranslationAnimator: the last frame of \"{anim.SourceText}\" is not what "
                    + "the reader sees at the end — the animation and TextScrambler disagree.");
            }
        }
    }

    // One run in the taught language.
    sealed class AnimatedSpan
    {
        // One planned move: `slot` takes `token`, which comes from `from` (whose
        // old occupant goes there in turn); `from` == -1 is a token appearing
        // (a punctuation mark coming back).
        readonly struct Move
        {
            public readonly int slot;
            public readonly int from;
            public readonly int token;

            public Move(int slot, int from, int token)
            {
                this.slot = slot;
                this.from = from;
                this.token = token;
            }
        }

        readonly LanguageText.Span _span;
        readonly TextScrambler.ScramblePlan _plan;
        readonly Tuning _tuning;
        // The tokens as the reader currently sees each slot.
        readonly int[] _occupant;
        // When each token's word / number started revealing; -inf = known from
        // the start, +inf = not yet.
        readonly float[] _wordRevealAt;
        readonly float[] _numberRevealAt;
        readonly List<Move> _moves = new();
        // Moves already applied; the one after them is the one fading.
        int _applied;
        TextScrambler.ScrambleState _state;

        public string SourceText => _span.Text;

        public AnimatedSpan(LanguageText.Span span, LanguageCoverage start, LanguageCoverage end, Tuning tuning)
        {
            _span = span;
            _tuning = tuning;
            _plan = new TextScrambler.ScramblePlan(span.Text, span.Language);
            _state = _plan.StateAt(start);
            _occupant = (int[])_state.occupant.Clone();
            _wordRevealAt = new float[_plan.Count];
            _numberRevealAt = new float[_plan.Count];
            for (int k = 0; k < _plan.Count; k++)
            {
                _wordRevealAt[k] = _state.wordKnown[k] ? float.NegativeInfinity : float.PositiveInfinity;
                _numberRevealAt[k] = _state.numberKnown[k] ? float.NegativeInfinity : float.PositiveInfinity;
            }
            PlanMoves(_state, _plan.StateAt(end));
        }

        // Every move from the start arrangement to the final one. Each puts the
        // right token into one slot, swapping out whatever was there; a slot
        // already fixed is never disturbed again (its token is not the one
        // anything later wants), so the moves end on the final arrangement.
        void PlanMoves(TextScrambler.ScrambleState start, TextScrambler.ScrambleState final)
        {
            var order = new List<int>();
            for (int s = 0; s < _plan.Count; s++)
            {
                if (final.held[s] && !start.held[s])
                {
                    order.Add(s);
                }
            }
            order.Sort((a, b) => _plan.PositionThreshold(a).CompareTo(_plan.PositionThreshold(b)));
            for (int s = 0; s < _plan.Count; s++)
            {
                if (!order.Contains(s))
                {
                    order.Add(s);
                }
            }
            int[] cur = (int[])start.occupant.Clone();
            foreach (int s in order)
            {
                int want = final.occupant[s];
                if (cur[s] == want)
                {
                    continue;
                }
                int from = want < 0 ? -1 : Array.IndexOf(cur, want);
                _moves.Add(new Move(s, from, want));
                Apply(cur, new Move(s, from, want));
            }
        }

        static void Apply(int[] occupant, Move move)
        {
            if (move.from >= 0)
            {
                occupant[move.from] = occupant[move.slot];
            }
            occupant[move.slot] = move.token;
        }

        // Each move has an equal share of the fill; it fades out, swaps at its
        // midpoint, and fades back in.
        float MoveSeconds => _moves.Count == 0 ? 0f : _tuning.fillSeconds / _moves.Count;

        float MoveStart(int i) => _tuning.FillStart + i * MoveSeconds;

        public void Step(float time, LanguageCoverage coverage)
        {
            _state = _plan.StateAt(coverage);
            for (int k = 0; k < _plan.Count; k++)
            {
                if (_state.wordKnown[k] && float.IsPositiveInfinity(_wordRevealAt[k]))
                {
                    _wordRevealAt[k] = time;
                }
                if (_state.numberKnown[k] && float.IsPositiveInfinity(_numberRevealAt[k]))
                {
                    _numberRevealAt[k] = time;
                }
            }
            while (_applied < _moves.Count && time >= MoveStart(_applied) + MoveSeconds * 0.5f)
            {
                Apply(_occupant, _moves[_applied]);
                _applied++;
            }
        }

        public void Finish(LanguageCoverage end)
        {
            _state = _plan.StateAt(end);
            while (_applied < _moves.Count)
            {
                Apply(_occupant, _moves[_applied]);
                _applied++;
            }
            for (int k = 0; k < _plan.Count; k++)
            {
                if (_state.wordKnown[k])
                {
                    _wordRevealAt[k] = float.NegativeInfinity;
                }
                if (_state.numberKnown[k])
                {
                    _numberRevealAt[k] = float.NegativeInfinity;
                }
            }
        }

        public bool MatchesFinal(float time, LanguageCoverage end)
        {
            TextScrambler.ScrambleState final = _plan.StateAt(end);
            var sb = new StringBuilder();
            sb.Append(_plan.Separator(0));
            for (int s = 0; s < _plan.Count; s++)
            {
                int k = _occupant[s];
                if (k >= 0)
                {
                    sb.Append(TokenAt(k, time));
                }
                sb.Append(_plan.Separator(s + 1));
            }
            return sb.ToString() == _plan.Flatten(final);
        }

        public string Bbcode(float time, Color textColor)
        {
            var sb = new StringBuilder();
            sb.Append(LanguageText.EscapeBbcode(_plan.Separator(0)));
            int fading = -1;
            float alpha = 1f;
            // The move whose window `time` is in — the last applied one in its
            // second half, the next one in its first.
            for (int i = Math.Max(0, _applied - 1); i <= _applied && i < _moves.Count; i++)
            {
                float u = MoveSeconds <= 0f ? 1f : (time - MoveStart(i)) / MoveSeconds;
                if (u >= 0f && u < 1f)
                {
                    fading = i;
                    alpha = Mathf.Abs(1f - 2f * u);
                }
            }
            for (int s = 0; s < _plan.Count; s++)
            {
                int k = _occupant[s];
                if (k >= 0)
                {
                    string text = LanguageText.EscapeBbcode(TokenAt(k, time));
                    bool fades = fading >= 0 && (s == _moves[fading].slot || s == _moves[fading].from);
                    if (fades && alpha < 1f)
                    {
                        Color c = textColor;
                        c.A *= alpha;
                        sb.Append("[color=#").Append(c.ToHtml(true)).Append(']').Append(text).Append("[/color]");
                    }
                    else
                    {
                        sb.Append(text);
                    }
                }
                sb.Append(LanguageText.EscapeBbcode(_plan.Separator(s + 1)));
            }
            return LanguageText.WrapSpanBbcode(_span, sb.ToString());
        }

        // Token k as it reads at `time`: its punctuation and capitals only once
        // it is home in a held position, and each letter / digit landing on its
        // translation at its own moment, the last by the time the meter fills.
        string TokenAt(int k, float time)
        {
            bool home = _state.held[k] && _occupant[k] == k;
            string text = _plan.BaseText(k, home);
            var sb = new StringBuilder(text.Length);
            int ordinal = 0;
            foreach (char c in text)
            {
                if (!char.IsLetterOrDigit(c))
                {
                    sb.Append(_plan.DisplayChar(k, c, true, true));
                    continue;
                }
                float h = Hash01(k, ordinal, 0x51ED27);
                float wordLand = Land(_wordRevealAt[k], h);
                float numberLand = Land(_numberRevealAt[k], h);
                bool isDigit = c >= '0' && c <= '9';
                bool wordShown = time >= wordLand;
                bool numberShown = time >= numberLand;
                float land = isDigit ? numberLand : wordLand;
                bool shown = isDigit ? numberShown : wordShown;
                if (!shown && !float.IsInfinity(land) && time >= land - _tuning.flickerSeconds)
                {
                    sb.Append(FlickerGlyph(k, ordinal, time));
                }
                else
                {
                    sb.Append(_plan.DisplayChar(k, c, wordShown, numberShown));
                }
                ordinal++;
            }
            return sb.ToString();
        }

        // When a character lands, given when its word started revealing: a
        // random point in the reveal window, the window cut short so nothing
        // lands after the meter is full.
        float Land(float revealAt, float h)
        {
            if (float.IsInfinity(revealAt))
            {
                return revealAt;
            }
            float window = Mathf.Min(_tuning.revealWindowSeconds, Mathf.Max(0f, _tuning.FillEnd - revealAt));
            return revealAt + window * h;
        }

        string FlickerGlyph(int k, int ordinal, float time)
        {
            int step = _tuning.flickerIntervalSeconds <= 0f ? 0 : (int)(time / _tuning.flickerIntervalSeconds);
            int slot = (int)(Hash01(k * 7919 + ordinal, step, 0x2F1A9) * TextScrambler.GlyphLetterCount);
            return _plan.GlyphBase > 0 ? TextScrambler.GlyphString(_plan.GlyphBase + slot) : ((char)('a' + slot)).ToString();
        }

        static float Hash01(int a, int b, int salt)
        {
            uint h = unchecked((uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u ^ (uint)salt);
            h ^= h >> 16;
            h = unchecked(h * 0x7FEB352Du);
            h ^= h >> 15;
            h = unchecked(h * 0x846CA68Bu);
            h ^= h >> 16;
            return (h >> 8) / 16777216f;
        }
    }
}
