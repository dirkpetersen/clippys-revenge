namespace ClippysRevenge.Core;

/// <summary>The one syntax check for SMTP addresses, shared by Recipients and Settings.</summary>
public static class EmailAddress
{
    public const int MaxLength = 254;
    private const string BadChars = "<>;,\"()[]\\";

    /// <summary>name@host: one '@', something on both sides, no whitespace, control or list/quote characters.</summary>
    public static bool IsValid(string? s)
    {
        if (s is null) return false;
        s = s.Trim();
        if (s.Length < 3 || s.Length > MaxLength) return false;
        int at = s.IndexOf('@');
        if (at <= 0 || at != s.LastIndexOf('@') || at == s.Length - 1) return false;
        foreach (var c in s)
            if (char.IsWhiteSpace(c) || char.IsControl(c) || BadChars.Contains(c)) return false;
        return true;
    }

    /// <summary>Trimmed and lower-cased; "" for null or blank.</summary>
    public static string Normalize(string? s) => MailboxIndexes.Normalize(s);
}
