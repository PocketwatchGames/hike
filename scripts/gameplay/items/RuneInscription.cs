using System.Collections.Generic;
using System.IO;

// The runes inscribed on one weapon or armor piece, in slot order. Matched
// against a species' sequence (SpeciesData.runes) like Mastermind: a rune in
// the right slot scores 1, a rune the sequence holds at another slot scores
// 0.5, and each sequence position is claimed at most once — so repeats count
// only as often as the sequence has them. Empty slots are null.
public class RuneInscription
{
    public readonly List<RuneData> slots = new();

    public bool IsEmpty
    {
        get
        {
            foreach (RuneData rune in slots)
            {
                if (rune != null)
                {
                    return false;
                }
            }
            return true;
        }
    }

    // 0..1: the match score over the sequence length. 0 for a species with no
    // sequence or an empty inscription.
    public float AffinityFor(SpeciesData species)
    {
        RuneData[] sequence = species?.RunesFlat;
        if (sequence == null || sequence.Length == 0 || slots.Count == 0)
        {
            return 0f;
        }
        // Up to 5 positions — stackalloc-sized flags, no allocation per hit.
        System.Span<bool> claimed = stackalloc bool[sequence.Length];
        System.Span<bool> placed = stackalloc bool[slots.Count];
        float score = 0f;
        for (int i = 0; i < slots.Count && i < sequence.Length; i++)
        {
            if (slots[i] != null && slots[i] == sequence[i])
            {
                score += 1f;
                claimed[i] = true;
                placed[i] = true;
            }
        }
        for (int i = 0; i < slots.Count; i++)
        {
            if (placed[i] || slots[i] == null)
            {
                continue;
            }
            for (int j = 0; j < sequence.Length; j++)
            {
                if (!claimed[j] && sequence[j] == slots[i])
                {
                    score += OutOfPlaceScore;
                    claimed[j] = true;
                    break;
                }
            }
        }
        return score / sequence.Length;
    }

    // Half the benefit of a rune in its right slot.
    private const float OutOfPlaceScore = 0.5f;

    // Inside an EntitySerializer item write (refs resolve against its table).
    public void Write(BinaryWriter w)
    {
        w.Write(slots.Count);
        foreach (RuneData rune in slots)
        {
            EntitySerializer.WriteRef(w, rune);
        }
    }

    public void Read(BinaryReader r)
    {
        slots.Clear();
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            slots.Add(EntitySerializer.ReadRef<RuneData>(r));
        }
    }

    public void CopyTo(RuneInscription dest)
    {
        dest.slots.Clear();
        dest.slots.AddRange(slots);
    }
}
