using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

/// <summary>Name is "" (never null) when the entry had no display name.</summary>
public sealed record Recipient(string Name, string Email);

public static partial class Recipients
{
    [GeneratedRegex(@"^(?<name>.*?)\s*<(?<email>[^<>]*)>$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AngleForm();

    public static IReadOnlyList<Recipient> Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var result = new List<Recipient>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(';'))
        {
            var entry = part.Trim();
            if (entry.Length == 0) continue;

            if (!TryParseEntry(entry, out var r))
                throw new FormatException(
                    $"Recipient \"{entry}\" has no valid email address. Use \"Name <email>\" or a bare email, and separate recipients with \";\".");

            if (seen.Add(r.Email)) result.Add(r);
        }
        return result;
    }

    /// <summary>One list entry ("Name &lt;email&gt;", "\"Name\" &lt;email&gt;", "&lt;email&gt;" or a bare email), trimmed.
    /// False for null, blank, or no valid address (EmailAddress.IsValid). Never throws.</summary>
    public static bool TryParseEntry(string? entry, [NotNullWhen(true)] out Recipient? recipient)
    {
        recipient = null;
        if (entry is null) return false;
        entry = entry.Trim();
        if (entry.Length == 0) return false;

        string name = "", email;
        var m = AngleForm().Match(entry);
        if (m.Success)
        {
            name = m.Groups["name"].Value.Trim();
            email = m.Groups["email"].Value.Trim();
            if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
                name = name[1..^1].Trim();
        }
        else
        {
            email = entry;
        }

        if (!EmailAddress.IsValid(email)) return false;
        recipient = new Recipient(name, email);
        return true;
    }

    public static string Format(IEnumerable<Recipient> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        return string.Join("; ", recipients.Select(r =>
            string.IsNullOrWhiteSpace(r.Name) ? r.Email : $"{r.Name} <{r.Email}>"));
    }
}
