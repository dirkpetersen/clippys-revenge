namespace ClippysRevenge.Core;

/// <summary>Builds a safe SQLite FTS5 MATCH expression from user/model free text (SPEC §9.2).</summary>
public static class FtsQuery
{
    private readonly record struct Item(string Text, bool IsOr, bool IsPrefix);

    /// <summary>
    /// Returns the MATCH expression, or null when the input has nothing searchable
    /// (null, empty, whitespace, or only punctuation/operators). Never throws.
    /// The result must be bound as a parameter (<c>emails_fts MATCH @q</c>), never
    /// concatenated into SQL. A null result means "no full-text filter": the caller
    /// must omit the MATCH clause entirely.
    /// </summary>
    public static string? Build(string? userText)
    {
        if (string.IsNullOrEmpty(userText))
        {
            return null;
        }

        var items = new List<Item>();
        var s = userText;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"')
            {
                var close = s.IndexOf('"', i + 1);
                if (close >= 0)
                {
                    items.Add(new Item(s.Substring(i + 1, close - i - 1).Trim(), false, false));
                    i = close + 1;
                }
                else
                {
                    i++;
                }
                continue;
            }
            if (IsSeparator(c))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < s.Length && s[i] != '"' && !IsSeparator(s[i]))
            {
                i++;
            }
            var token = s.Substring(start, i - start);
            if (token == "OR")
            {
                items.Add(new Item(token, true, false));
                continue;
            }
            var stripped = token.TrimEnd('*');
            items.Add(new Item(stripped, false, stripped.Length != token.Length));
        }

        // Drop junk terms (no letter or digit) before OR handling.
        var seq = items.Where(it => it.IsOr || it.Text.Any(char.IsLetterOrDigit)).ToList();

        var groups = new List<List<Item>>();
        List<Item>? current = null;
        var pendingOr = false;
        foreach (var it in seq)
        {
            if (it.IsOr)
            {
                pendingOr = current is not null;
                continue;
            }
            if (pendingOr && current is not null)
            {
                current.Add(it);
            }
            else
            {
                current = new List<Item> { it };
                groups.Add(current);
            }
            pendingOr = false;
        }

        if (groups.Count == 0)
        {
            return null;
        }

        return string.Join(" AND ", groups.Select(g =>
            g.Count == 1 ? Render(g[0]) : "(" + string.Join(" OR ", g.Select(Render)) + ")"));
    }

    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || char.IsControl(c);

    private static string Render(Item it) =>
        "\"" + it.Text.Replace("\"", "\"\"") + "\"" + (it.IsPrefix ? "*" : "");
}
