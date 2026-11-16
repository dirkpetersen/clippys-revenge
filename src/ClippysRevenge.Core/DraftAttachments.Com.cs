namespace ClippysRevenge.Core;

/// <summary>Pure helpers for OutlookMail.ReadAttachmentForAi (SPEC 26.3 "Cloud attachments in Outlook COM", 26.9).</summary>
public static class AiAttachmentCom
{
    public const string PrAttachMethod = "http://schemas.microsoft.com/mapi/proptag/0x37050003";
    public const string PrAttachLongFileName = "http://schemas.microsoft.com/mapi/proptag/0x3707001F";
    public const string PrAttachLongPathName = "http://schemas.microsoft.com/mapi/proptag/0x370D001F";
    public const int AttachByWebReference = 7;
    public const int OlByValue = 1, OlByReference = 4, OlEmbeddedItem = 5;   // olAttachmentType values
    public const int MaxItemAttachmentNames = 50;

    /// <summary>True when attachMethod is int or long 7, or type == OlByReference. Anything else false.</summary>
    public static bool IsCloud(object? attachMethod, int type) =>
        type == OlByReference
        || attachMethod is int i && i == AttachByWebReference
        || attachMethod is long l && l == AttachByWebReference;

    /// <summary>The first argument that is a string which, trimmed, is an absolute https URL
    /// (Uri.TryCreate Absolute, Scheme https); else null. Never throws.</summary>
    public static string? CloudUrl(object? longFileName, object? longPathName)
    {
        foreach (var candidate in new[] { longFileName, longPathName })
        {
            if (candidate is not string s) continue;
            var t = s.Trim();
            if (t.Length == 0) continue;
            try
            {
                if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) return t;
            }
            catch (Exception)
            {
                // Never throws.
            }
        }
        return null;
    }

    /// <summary>Builds the Item record: From = "Name &lt;address&gt;" when both are non-blank (trimmed),
    /// else whichever is non-blank, else null. Body cut to AttachmentText.MaxFileChars (20,000) chars,
    /// one less when the cut would split a surrogate pair; null stays null. Names: null gives [];
    /// at most MaxItemAttachmentNames, in order. Never throws.</summary>
    public static AiAttachmentRead.Item Item(string? subject, string? senderName, string? senderAddress,
        DateTimeOffset? sent, string? body, IReadOnlyList<string>? attachmentNames)
    {
        var name = senderName?.Trim();
        var address = senderAddress?.Trim();
        var hasName = !string.IsNullOrEmpty(name);
        var hasAddress = !string.IsNullOrEmpty(address);
        var from = hasName && hasAddress ? $"{name} <{address}>" : hasName ? name : hasAddress ? address : null;

        var text = body;
        if (text is { Length: > AttachmentText.MaxFileChars })
        {
            var keep = AttachmentText.MaxFileChars;
            if (char.IsHighSurrogate(text[keep - 1]) && char.IsLowSurrogate(text[keep])) keep--;
            text = text[..keep];
        }

        IReadOnlyList<string> names = attachmentNames is null ? [] : attachmentNames.Take(MaxItemAttachmentNames).ToList();
        return new AiAttachmentRead.Item(subject, from, sent, text, names);
    }
}
