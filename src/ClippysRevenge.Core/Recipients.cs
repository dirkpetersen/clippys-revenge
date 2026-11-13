using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

/// <summary>Name is "" (never null) when the entry had no display name.</summary>
public sealed record Recipient(string Name, string Email);

public static partial class Recipients
{
    [GeneratedRegex(@"^(?<name>.*?)\s*<(?<email>[^<>]*)>$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AngleForm();

    private static readonly char[] BadEmailChars = ['<', '>', '"', ',', ';'];

    public static IReadOnlyList<Recipient> Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var result = new List<Recipient>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(';'))
        {
            var entry = part.Trim();
            if (entry.Length == 0) continue;

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

            if (!IsValidEmail(email))
                throw new FormatException(
                    $"Recipient \"{entry}\" has no valid email address. Use \"Name <email>\" or a bare email, and separate recipients with \";\".");

            if (seen.Add(email)) result.Add(new Recipient(name, email));
        }
        return result;
    }

    public static string Format(IEnumerable<Recipient> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        return string.Join("; ", recipients.Select(r =>
            string.IsNullOrWhiteSpace(r.Name) ? r.Email : $"{r.Name} <{r.Email}>"));
    }

    private static bool IsValidEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1) return false;
        if (email.IndexOf('@', at + 1) >= 0) return false;
        if (email.IndexOfAny(BadEmailChars) >= 0) return false;
        foreach (var c in email)
            if (char.IsWhiteSpace(c)) return false;
        return true;
    }
}
