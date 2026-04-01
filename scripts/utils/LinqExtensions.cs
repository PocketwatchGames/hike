using System;
using System.Collections.Generic;

public static class LinqExtensions
{
    public static TSource MaxBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> selector)
    {
        using (var iterator = source.GetEnumerator())
        {
            if (!iterator.MoveNext())
            {
                throw new InvalidOperationException("Sequence contains no elements");
            }

            var maxElement = iterator.Current;
            var maxValue = selector(maxElement);
            var comparer = Comparer<TKey>.Default;

            while (iterator.MoveNext())
            {
                var element = iterator.Current;
                var value = selector(element);
                if (comparer.Compare(value, maxValue) > 0)
                {
                    maxElement = element;
                    maxValue = value;
                }
            }

            return maxElement;
        }
    }

    public static TSource MinBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> selector)
    {
        using (var iterator = source.GetEnumerator())
        {
            if (!iterator.MoveNext())
            {
                throw new InvalidOperationException("Sequence contains no elements");
            }

            var minElement = iterator.Current;
            var minValue = selector(minElement);
            var comparer = Comparer<TKey>.Default;

            while (iterator.MoveNext())
            {
                var element = iterator.Current;
                var value = selector(element);
                if (comparer.Compare(value, minValue) < 0)
                {
                    minElement = element;
                    minValue = value;
                }
            }

            return minElement;
        }
    }

    public static int MaxIndexBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> selector)
    {
        using (var iterator = source.GetEnumerator())
        {
            if (!iterator.MoveNext())
            {
                throw new InvalidOperationException("Sequence contains no elements");
            }

            int maxIndex = 0;
            var maxValue = selector(iterator.Current);
            var comparer = Comparer<TKey>.Default;
            int index = 0;

            while (iterator.MoveNext())
            {
                index++;
                var value = selector(iterator.Current);
                if (comparer.Compare(value, maxValue) > 0)
                {
                    maxIndex = index;
                    maxValue = value;
                }
            }

            return maxIndex;
        }
    }

    public static int MinIndexBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> selector)
    {
        using (var iterator = source.GetEnumerator())
        {
            if (!iterator.MoveNext())
            {
                throw new InvalidOperationException("Sequence contains no elements");
            }

            int minIndex = 0;
            var minValue = selector(iterator.Current);
            var comparer = Comparer<TKey>.Default;
            int index = 0;

            while (iterator.MoveNext())
            {
                index++;
                var value = selector(iterator.Current);
                if (comparer.Compare(value, minValue) < 0)
                {
                    minIndex = index;
                    minValue = value;
                }
            }

            return minIndex;
        }
    }

    public static void RemoveAtSwap<T>(this IList<T> list, int index)
    {
        list[index] = list[list.Count - 1];
        list.RemoveAt(list.Count - 1);
    }

    public static bool RemoveSwap<T>(this IList<T> list, T item)
    {
        int index = list.IndexOf(item);
        if (index >= 0)
        {
            list.RemoveAtSwap(index);
            return true;
        }
        return false;
    }

}
