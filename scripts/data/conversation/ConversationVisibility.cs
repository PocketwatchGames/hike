using Godot;

// Filter helper for ConversationResponse — decides whether a response is
// shown to the player by combining the authored ConversationCondition hard
// gate with a stable, comprehension-driven probabilistic gate.
//
// A response carrying its own ConversationResponse.language is exempt from the
// branch half of that min (see Compute) — it is not spoken in the NPC's tongue,
// so nothing about their line gates it.
//
// The probabilistic side takes the MIN of two LanguageText.Comprehension
// scores: how well the player understood the NPC's BRANCH text leading up
// to this point, AND how well they understand the RESPONSE itself in the
// same language. The weaker of the two caps the chance — so a player who
// only half-understood the question can still see a fully-readable answer
// at no worse than 50%, but a half-readable answer never beats 50% no
// matter how clear the question was. The roll itself is hashed by the
// response's textLocKey so a given response has a stable threshold; as the
// player learns more language components, both factors climb and
// previously-hidden options pop into the menu.
public static class ConversationVisibility
{
    // Aggregate comprehension across every line in `branch.lineLocKeys`
    // (simple average of per-line LanguageText.Comprehension), returned as
    // [0,1]. `language` is the DEFAULT language of the branch text; a line
    // may still switch tongues mid-sentence via [lang:...] spans, which is
    // why there's no "fluent in `language`, so score 1" short-circuit here.
    // Callers pre-compute this once per chooser pass so we don't re-walk
    // the branch text for every response. `grammarWeight` comes from
    // SimData.LanguageGrammarWeight.
    public static float ComputeBranchComprehension(ConversationBranch branch, LanguageData language, Player player, float grammarWeight)
    {
        if (branch?.lineLocKeys == null || branch.lineLocKeys.Count == 0)
        {
            return 1f;
        }
        if (player == null)
        {
            return 1f;
        }
        int validLines = 0;
        float sum = 0f;
        for (int i = 0; i < branch.lineLocKeys.Count; i++)
        {
            StringName key = branch.lineLocKeys[i];
            if (key == default || key == "")
            {
                continue;
            }
            string text = Loc.Get(key);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            sum += LanguageText.Comprehension(text, language, player, grammarWeight);
            validLines++;
        }
        return validLines > 0 ? sum / validLines : 1f;
    }

    // Full visibility computation for a response, including the values
    // used to make the decision. Callers needing only the boolean can
    // read `.Visible`; debug overlays use `.CombinedScore` and `.Roll`
    // directly.
    public readonly struct ResponseVisibilityResult
    {
        // The authored ConversationCondition allowed this response. False
        // = hard-gated; runtime should hide it entirely (debug too).
        public readonly bool ConditionPassed;
        // Outcome of the language-comprehension roll. Only meaningful
        // when ConditionPassed is true.
        public readonly bool RollVisible;
        // min(branchComp, responseComp) used as the visibility
        // threshold. 1f for short-circuit cases (silent / no-language /
        // full-fluency) where no roll actually happened.
        public readonly float CombinedScore;
        // The stable per-response RNG roll [0, 1). 0 for short-circuit
        // cases.
        public readonly float Roll;

        public bool Visible => ConditionPassed && RollVisible;

        public ResponseVisibilityResult(bool conditionPassed, bool rollVisible, float combinedScore, float roll)
        {
            ConditionPassed = conditionPassed;
            RollVisible = rollVisible;
            CombinedScore = combinedScore;
            Roll = roll;
        }
    }

    // Compute visibility + diagnostics for a response. `branchComprehension`
    // is the pre-computed branch score from ComputeBranchComprehension;
    // pass 1f to skip the branch factor entirely. `grammarWeight` comes
    // from SimData.LanguageGrammarWeight.
    public static ResponseVisibilityResult Compute(ConversationResponse response, ConversationContext ctx, LanguageData branchLanguage, float branchComprehension, float grammarWeight)
    {
        if (response == null)
        {
            return new(false, false, 0f, 0f);
        }
        if (response.condition != null && !response.condition.Evaluate(ctx))
        {
            return new(false, false, 0f, 0f);
        }

        // No language-scramble gate when there's nothing to scramble: a
        // silent response (no loc key) or no listener. Everything else scores
        // per span, since even an unlanguaged line can quote a tongue the
        // player doesn't have.
        StringName key = response.textLocKey;
        if (key == default || key == "")
        {
            return new(true, true, 1f, 0f);
        }
        if (ctx.player == null)
        {
            return new(true, true, 1f, 0f);
        }
        LanguageData lang = response.ResolveLanguage(branchLanguage ?? ctx.speakerLanguage);

        string text = Loc.Get(key);
        float responseComprehension = LanguageText.Comprehension(text, lang, ctx.player, grammarWeight);
        // Min(branch, response) — the bottleneck axis caps visibility.
        // Cleaner falloff than the product (50% × 50% = 25%) while still
        // gating responses behind whichever side the player understands
        // least.
        //
        // A response with its OWN language drops the branch factor: it isn't an
        // answer in the speaker's tongue, so "how much of their line did you
        // follow" has no bearing on whether you can utter it. That's what makes
        // a common-tongue "Do you speak the common tongue?" survive a greeting
        // you understood none of. Its own score still applies, so the player
        // must actually speak whatever it's written in.
        float combined = response.language != null
            ? responseComprehension
            : Mathf.Min(branchComprehension, responseComprehension);

        // Per-response RNG seeded by the loc key only. Same response →
        // same threshold across the run, so a response that's hidden at
        // a given combined score stays hidden until the player learns
        // enough to push the score past it.
        int seed = TextScrambler.StableSeed(key.ToString());
        System.Random rng = new System.Random(seed);
        float roll = (float)rng.NextDouble();
        return new(true, roll < combined, combined, roll);
    }
}
