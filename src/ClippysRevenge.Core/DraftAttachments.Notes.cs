namespace ClippysRevenge.Core;

/// <summary>SPEC 26.7 note texts (without the "Clippy: " prefix). Pure; never logs.</summary>
public static class AttachmentNoteTexts
{
    public const int MaxNameChars = 80;
    public const string NoName = "(no name)";

    /// <summary>DraftReasonLine.CleanText(name, MaxNameChars); NoName when that is "". Not escaped. Never throws.</summary>
    public static string CleanName(string? name)
    {
        var n = DraftReasonLine.CleanText(name, MaxNameChars);
        return n.Length == 0 ? NoName : n;
    }

    /// <summary>The sentence for one note, not escaped; null when the note gets no line (null note, unknown kind, Format
    /// with a bad extension). Never throws.</summary>
    public static string? Text(AttachmentNote? note)
    {
        try
        {
            if (note is null) return null;
            var n = CleanName(note.Name);
            switch (note.Kind)
            {
                case AttachmentNoteKind.NotSynced: return $"Couldn't read the shared file {n} (not synced to this PC).";
                case AttachmentNoteKind.FromOutside:
                    return $"Couldn't read the shared file {n} (Clippy only opens shared files from people in your organization).";
                case AttachmentNoteKind.EarlierEmailGone:
                    return $"Couldn't read {n} (the earlier email is no longer in your mailbox).";
                case AttachmentNoteKind.PasswordProtected: return $"Couldn't read {n} (it's password-protected).";
                case AttachmentNoteKind.TooLarge: return $"Couldn't read {n} (larger than 25 MB).";
                case AttachmentNoteKind.NoText: return $"Couldn't read {n} (no text in it, maybe a scan).";
                case AttachmentNoteKind.Format:
                    var ext = note.Extension;
                    if (AttachmentReasons.CantRead(ext ?? "") == AttachmentReasons.UnknownKind) return null;
                    return $"Couldn't read {n} (Clippy can't read .{ext} files).";
                case AttachmentNoteKind.Damaged: return $"Couldn't read {n} (the file seems damaged).";
                case AttachmentNoteKind.TookTooLong: return $"Couldn't read {n} (it took too long).";
                case AttachmentNoteKind.TooMuch: return $"Couldn't read {n} (too much attachment text for one draft).";
                case AttachmentNoteKind.Truncated: return $"Read only the start of {n} (it's long).";
                default: return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"1 more file couldn't be read fully." for 1, else "&lt;n&gt; more files couldn't be read fully.".</summary>
    /// <exception cref="ArgumentOutOfRangeException">n is less than 1.</exception>
    public static string More(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        return n == 1
            ? "1 more file couldn't be read fully."
            : $"{n.ToString(System.Globalization.CultureInfo.InvariantCulture)} more files couldn't be read fully.";
    }
}

/// <summary>SPEC 26.7: the yellow notes at the top of an AI draft. Pure; never logs.</summary>
public static class AttachmentNotes
{
    public const int MaxNotes = 5;

    /// <summary>The note lines concatenated, each exactly like DraftReasonLine.Html's markup; "" for null/empty or when
    /// no note gives a line. Never throws.</summary>
    public static string Html(IReadOnlyList<AttachmentNote?>? notes)
    {
        try
        {
            if (notes is null || notes.Count == 0) return "";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var texts = new List<string>();
            foreach (var note in notes)
            {
                var t = AttachmentNoteTexts.Text(note);
                if (t is not null && seen.Add(t)) texts.Add(t);
            }
            var sb = new System.Text.StringBuilder();
            foreach (var t in texts.Take(MaxNotes)) sb.Append(DraftReasonLine.Wrap(t));
            if (texts.Count > MaxNotes) sb.Append(DraftReasonLine.Wrap(AttachmentNoteTexts.More(texts.Count - MaxNotes)));
            return sb.ToString();
        }
        catch (Exception)
        {
            return "";
        }
    }
}
