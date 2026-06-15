using System;
using System.Collections.Generic;

// A weighted random-selection list (a weighted bag). Each entry pairs an item
// with a weight; Choose()/ChooseAndRemove() draw an item with probability
// proportional to its weight. Adding an entry accumulates into TotalWeight so
// a draw is a single walk of the list — roll a number in [0, TotalWeight],
// subtract each entry's weight in turn, and return the entry that drives the
// running total to <= 0. ChooseAndRemove additionally drops the drawn entry,
// giving sampling without replacement.
public class WeightedList<T>
{
    private struct Entry
    {
        public float Weight;
        public T Item;
    }

    private readonly List<Entry> _entries = new();
    private float _totalWeight;

    // Sum of every entry's weight. A roll for Choose is taken from
    // [0, TotalWeight].
    public float TotalWeight => _totalWeight;

    public int Count => _entries.Count;

    // Empty the list for reuse, keeping the backing buffer so refilling in a
    // hot loop doesn't reallocate.
    public void Clear()
    {
        _entries.Clear();
        _totalWeight = 0f;
    }

    // Add an item with the given weight. Non-positive weights are ignored so a
    // 0-weight entry can never be chosen.
    public void Add(T item, float weight)
    {
        if (weight <= 0f)
        {
            return;
        }
        _entries.Add(new Entry { Weight = weight, Item = item });
        _totalWeight += weight;
    }

    // Pick an item given a pre-rolled random number in [0, TotalWeight].
    // Walks the list subtracting each weight from the roll and returns the
    // entry that takes it to <= 0. Returns default(T) if the list is empty.
    public T Choose(float roll)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            roll -= _entries[i].Weight;
            if (roll <= 0f)
            {
                return _entries[i].Item;
            }
        }
        // Roll landed past the accumulated total (floating-point slop, or an
        // empty list) — fall back to the last entry when one exists.
        return _entries.Count > 0 ? _entries[_entries.Count - 1].Item : default;
    }

    // Convenience overload: rolls [0, TotalWeight) from the supplied RNG.
    public T Choose(Random rng)
    {
        return Choose((float)rng.NextDouble() * _totalWeight);
    }

    // Pick an item as Choose does, then remove it so it can't be drawn again
    // (TotalWeight drops by its weight). Returns default(T) if empty.
    public T ChooseAndRemove(float roll)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            roll -= _entries[i].Weight;
            if (roll <= 0f)
            {
                T item = _entries[i].Item;
                _totalWeight -= _entries[i].Weight;
                _entries.RemoveAt(i);
                return item;
            }
        }
        if (_entries.Count == 0)
        {
            return default;
        }
        int last = _entries.Count - 1;
        T fallback = _entries[last].Item;
        _totalWeight -= _entries[last].Weight;
        _entries.RemoveAt(last);
        return fallback;
    }

    // Convenience overload: rolls [0, TotalWeight) from the supplied RNG.
    public T ChooseAndRemove(Random rng)
    {
        return ChooseAndRemove((float)rng.NextDouble() * _totalWeight);
    }
}
