using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>One submenu line. Label: plain text (tests, logs never). Text: the same for a WinForms menu ('&amp;' doubled).
/// Entry is null for the "and n more" line.</summary>
public sealed record DraftInviteMenuLine(string Label, string Text, DraftInviteEntry? Entry);

/// <summary>The submenu model (SPEC 22.3). Lines are meetings in order, then the optional "and n more" line.
/// LinesEnabled: meeting lines are clickable. StartOutlookHint: add the last disabled hint line.</summary>
public sealed record DraftInviteMenuModel(
    bool Visible, string Header, IReadOnlyList<DraftInviteMenuLine> Lines, bool LinesEnabled, bool StartOutlookHint);

/// <summary>Pure model of the tray's Draft invites submenu (SPEC 6.2, 22.3). No COM, no WinForms.</summary>
public static class DraftInviteMenu
{
    public const int MaxLines = 15;
    public const int MaxSubjectLength = 40;
    public const int MaxMailboxNameLength = 40;
    public const string StartOutlookHintText = "Start Outlook to open them";
    public const string NoSubject = "(no subject)";
    public const string GoneText = "That draft invite is no longer in your calendar.";
    public const string OpenFailedText = "It looks like Outlook couldn't open that draft invite. Open it from your calendar.";

    /// <summary>"Draft invites (n)", n invariant without grouping.</summary>
    public static string Header(int count) => string.Create(CultureInfo.InvariantCulture, $"Draft invites ({count})");

    /// <summary>"and n more".</summary>
    public static string More(int n) => string.Create(CultureInfo.InvariantCulture, $"and {n} more");

    /// <exception cref="ArgumentNullException">pending, shared or timeZone is null.</exception>
    public static DraftInviteMenuModel Build(IReadOnlyList<PendingDraftInvite> pending, OutlookConnectionState state,
        IEnumerable<SharedMailbox?> shared, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(timeZone);

        var items = pending.Where(p => p is not null).ToList();
        var sorted = new List<PendingDraftInvite>(items.Count);
        sorted.AddRange(items.Where(p => p.Details is not null).OrderBy(p => p.Details!.Start).ThenBy(p => p.Details!.End));
        sorted.AddRange(items.Where(p => p.Details is null).OrderBy(p => p.Entry.CreatedUtc));

        var sharedList = shared.ToList();
        var lines = new List<DraftInviteMenuLine>();
        foreach (var p in sorted.Take(MaxLines))
        {
            var label = Label(p, sharedList, timeZone);
            lines.Add(new DraftInviteMenuLine(label, MenuText(label), p.Entry));
        }
        if (sorted.Count > MaxLines)
        {
            var more = More(sorted.Count - MaxLines);
            lines.Add(new DraftInviteMenuLine(more, MenuText(more), null));
        }

        var available = state == OutlookConnectionState.Available;
        return new DraftInviteMenuModel(sorted.Count > 0, Header(sorted.Count), lines, available,
            state == OutlookConnectionState.Unavailable);
    }

    /// <summary>One line's plain label.</summary>
    public static string Label(PendingDraftInvite p, IEnumerable<SharedMailbox?> shared, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(timeZone);

        string label;
        if (p.Details is { } d)
        {
            var subject = Clean(d.Subject);
            if (subject.Length == 0) subject = NoSubject;
            label = InviteTexts.When(d.Start, d.End) + "  " + TrayText.Truncate(subject, MaxSubjectLength);
        }
        else
        {
            var local = TimeZoneInfo.ConvertTime(p.Entry.CreatedUtc, timeZone);
            label = "Draft invite saved " + string.Create(CultureInfo.InvariantCulture, $"{local:ddd} {local:M/d} {local:HH:mm}");
        }

        var mailbox = p.Entry.Mailbox;
        if (!string.IsNullOrWhiteSpace(mailbox))
        {
            var found = shared.FirstOrDefault(m => m is not null
                && string.Equals(MailboxIndexes.Normalize(m.Smtp), mailbox, StringComparison.OrdinalIgnoreCase));
            var name = Clean(found?.DisplayName);
            if (name.Length == 0) name = mailbox;
            label += " (" + TrayText.Truncate(name, MaxMailboxNameLength) + ")";
        }
        return label;
    }

    /// <summary>Replaces control characters with spaces, collapses whitespace runs to one space, trims.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        var lastSpace = false;
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(c);
                lastSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Doubles every '&amp;' (WinForms mnemonic escape).</summary>
    public static string MenuText(string label) => label.Replace("&", "&&", StringComparison.Ordinal);
}

public enum DraftInviteOpenResult { Opened, Gone }

/// <summary>COM seam for opening a tracked meeting (App: OutlookDraftInviteWindow).</summary>
public interface IDraftInviteWindow
{
    /// <summary>One Interactive COM work item: look the meeting up and Display() it (non-modal).</summary>
    /// <returns>Opened, or Gone when the item no longer exists, isn't an appointment, or is in Deleted Items.</returns>
    /// <exception cref="OutlookUnavailableException">Outlook is gone or not responding (also the subclass).</exception>
    /// <exception cref="OperationCanceledException">ct fired before the work item ran.</exception>
    /// <remarks>Any other exception: the meeting couldn't be opened (kept tracked).</remarks>
    Task<DraftInviteOpenResult> OpenAsync(DraftInviteEntry entry, CancellationToken ct);
}

/// <summary>What a click on a submenu line does (SPEC 22.3), without WinForms.</summary>
public static class DraftInviteClick
{
    /// <summary>Opens the meeting; returns the click-feedback balloon text, or null for none. Never throws (except
    /// ArgumentNullException for null arguments). Calls <paramref name="forget"/> only for Gone.</summary>
    public static async Task<string?> OpenAsync(IDraftInviteWindow window, DraftInviteEntry entry, Func<string, bool> forget,
        ILogger? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(forget);
        try
        {
            var result = await window.OpenAsync(entry, ct).ConfigureAwait(false);
            if (result == DraftInviteOpenResult.Opened)
            {
                log?.LogDebug("Draft invite opened.");
                return null;
            }
            try
            {
                forget(entry.EntryId);
            }
            catch (Exception ex)
            {
                log?.LogWarning("Forgetting a draft invite failed ({Type}).", ex.GetType().Name);
            }
            log?.LogInformation("Draft invite is gone; no longer tracked.");
            return DraftInviteMenu.GoneText;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (OutlookNotRespondingException)
        {
            log?.LogInformation("Opening a draft invite failed: Outlook is not available.");
            return BalloonTexts.OutlookNotResponding;
        }
        catch (OutlookUnavailableException)
        {
            log?.LogInformation("Opening a draft invite failed: Outlook is not available.");
            return BalloonTexts.OutlookUnavailable;
        }
        catch (Exception ex)
        {
            log?.LogWarning("Opening a draft invite failed ({Type}).", ex.GetType().Name);
            return DraftInviteMenu.OpenFailedText;
        }
    }
}
