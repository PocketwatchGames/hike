using System;

// A language is learned in five independent pieces. Each missing piece
// triggers a separate scrambling transform when text in that language is
// rendered to the player, so a partially-learned language reads with some
// pieces intact and others gibberish. A KnowledgeStone (or any other
// teaching source) grants one or more components at once via
// Player.LearnLanguageComponents; the missing-set drives TextScrambler.
[Flags]
public enum ELanguageComponents
{
    None = 0,
    // Word order. Without it, the scrambler permutes the order of
    // whitespace-bounded tokens WITHIN each sentence (sentences are
    // bounded by tokens ending in . ! ?). Tokens never cross a sentence
    // boundary, so a multi-sentence line stays sentence-ordered even when
    // individual sentences are scrambled.
    Grammar = 1 << 0,
    // Numerals. Without it, every digit in the text is replaced with a
    // stable per-language letter substitution — so "12" reads as letters.
    Numbers = 1 << 1,
    // Vocabulary buckets. Each word with letters falls into exactly one
    // of three buckets via a stable hash of its letters (case-insensitive,
    // punctuation-stripped). Missing the Vocabulary_N for a word's bucket
    // pushes that word's letters through the per-language substitution
    // cipher (digits/punctuation pass through unchanged); learning it
    // reveals those words in their original glyphs. Roughly a third of
    // any text resolves per Vocabulary component learned.
    Vocabulary1 = 1 << 2,
    Vocabulary2 = 1 << 3,
    Vocabulary3 = 1 << 4,
    All = Grammar | Numbers | Vocabulary1 | Vocabulary2 | Vocabulary3,
}
