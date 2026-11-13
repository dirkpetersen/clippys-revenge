namespace ClippysRevenge.Core;

/// <summary>The ClippyDraft named property (SPEC 19.4).</summary>
public static class DraftMarker
{
    /// <summary>The property's name inside PS_PUBLIC_STRINGS.</summary>
    public const string Name = "ClippyDraft";

    /// <summary>DASL name for PropertyAccessor.GetProperty/SetProperty and Table columns.</summary>
    public const string PropertyName =
        "http://schemas.microsoft.com/mapi/string/{00020329-0000-0000-C000-000000000046}/ClippyDraft";

    public const string Ai = "ai";
    public const string Tool = "tool";

    /// <summary>"ai" or "tool" (case-insensitive, no trimming) gives the lower-case constant; anything else, including null, gives null.</summary>
    public static string? Normalize(string? value)
    {
        if (string.Equals(value, Ai, StringComparison.OrdinalIgnoreCase)) return Ai;
        if (string.Equals(value, Tool, StringComparison.OrdinalIgnoreCase)) return Tool;
        return null;
    }
}
