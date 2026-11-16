namespace ClippysRevenge.Core;

public sealed record BudgetItem(int Tier, int Length);

/// <summary>SPEC 26.5 sharing of the attachment text budget across the three priority tiers.</summary>
public static class AttachmentBudget
{
    public const int MaxTotalChars = 40_000;
    public const int MinShareChars = 2_000;

    /// <summary>SPEC 26.5 allocation. Result[i] is the number of characters item i keeps, or null when it is
    /// skipped ("too much attachment text for one draft"). Items of one tier keep their input order.</summary>
    /// <exception cref="ArgumentNullException">items is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">a Tier outside 1..3, a negative Length, or total &lt; 0.</exception>
    public static IReadOnlyList<int?> Allocate(IReadOnlyList<BudgetItem> items, int total = MaxTotalChars)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        foreach (var it in items)
        {
            ArgumentNullException.ThrowIfNull(it);
            ArgumentOutOfRangeException.ThrowIfLessThan(it.Tier, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(it.Tier, 3);
            ArgumentOutOfRangeException.ThrowIfNegative(it.Length);
        }

        var result = new int?[items.Count];
        long remaining = total;
        for (var tier = 1; tier <= 3; tier++)
        {
            var idx = new List<int>();
            for (var i = 0; i < items.Count; i++)
                if (items[i].Tier == tier) idx.Add(i);
            if (idx.Count == 0) continue;

            long sum = 0;
            var maxLen = 0;
            foreach (var i in idx)
            {
                sum += items[i].Length;
                maxLen = Math.Max(maxLen, items[i].Length);
            }

            if (sum <= remaining)
            {
                foreach (var i in idx) result[i] = items[i].Length;
                remaining -= sum;
                continue;
            }

            // Largest whole L with sum(min(len, L)) <= remaining.
            var lo = 0;
            var hi = maxLen;
            while (lo < hi)
            {
                var mid = lo + (hi - lo + 1) / 2;
                long s = 0;
                foreach (var i in idx) s += Math.Min(items[i].Length, mid);
                if (s <= remaining) lo = mid;
                else hi = mid - 1;
            }

            if (lo >= MinShareChars)
            {
                foreach (var i in idx)
                {
                    var keep = Math.Min(items[i].Length, lo);
                    result[i] = keep;
                    remaining -= keep;
                }
                continue;
            }

            foreach (var i in idx)
            {
                var len = items[i].Length;
                if (len == 0) result[i] = 0;
                else if (remaining >= MinShareChars)
                {
                    var keep = Math.Min(len, MinShareChars);
                    result[i] = keep;
                    remaining -= keep;
                }
                else result[i] = null;
            }
        }
        return result;
    }

    /// <summary>text unchanged when keep &gt;= text.Length; else the first keep-1 chars (one fewer when that would
    /// split a surrogate pair) + the ellipsis, so the result is at most keep chars. keep &lt; 1 gives "".</summary>
    public static string Cut(string text, int keep)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (keep < 1) return "";
        if (keep >= text.Length) return text;
        var n = keep - 1;
        if (n > 0 && char.IsHighSurrogate(text[n - 1])) n--;
        return text[..n] + "…";
    }
}
