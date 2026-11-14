using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>Checks a reply_to_id before a meeting is created (SPEC §22.4). App: OutlookReplyDraftWriter.</summary>
public interface IReplyTargetCheck
{
    /// <summary>COM, Interactive. Resolves the email by the SPEC §8 GetItem chain and releases it; creates nothing.
    /// Returns the normalized mailbox key the email is in ("" = own).</summary>
    /// <exception cref="ItemNotFoundException">No such email in the own or an enabled shared mailbox.</exception>
    /// <exception cref="OutlookUnavailableException">Outlook is not running / not responding.</exception>
    /// <exception cref="OperationCanceledException">ct fired.</exception>
    Task<string> CheckAsync(string id, CancellationToken ct);
}

/// <summary>The note text (SPEC §22.4). Fixed English. Pure.</summary>
public static class MeetingNoteText
{
    public const int MaxSubjectChars = 80;
    public const int MaxReasonChars = 400;
    public const string SavedSuffix = " A note was saved in Drafts as a reply to the email.";
    public const string UnexpectedError = "an unexpected error";

    /// <summary>The note as plain text, including the [[ and ]] brackets; not HTML-escaped.</summary>
    /// <param name="timeZoneLabel">Already resolved (TimeZoneLabels.Resolve); null/blank = no label.</param>
    /// <param name="calendarMailbox">The meeting's normalized mailbox ("" = own calendar).</param>
    /// <exception cref="ArgumentNullException">subject or calendarMailbox null.</exception>
    public static string Text(DateTime start, DateTime end, string? timeZoneLabel, string subject, string calendarMailbox)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(calendarMailbox);
        var when = InviteTexts.When(start, end);
        if (!string.IsNullOrWhiteSpace(timeZoneLabel)) when += " " + timeZoneLabel.Trim();
        var cal2 = calendarMailbox.Length == 0 ? "your calendar" : "that calendar";
        return $"[[Draft invite saved in {InviteTexts.Calendar(calendarMailbox)}: {when} \"{CleanSubject(subject)}\". "
               + $"Open it from the Clippy tray (Draft invites) or {cal2}, add the Teams or Zoom link and send it. "
               + "Delete this note before you send this reply.]]";
    }

    /// <summary>"&lt;p&gt;" + HighlightOpen + Escape(Text(...)) + HighlightClose + "&lt;/p&gt;&lt;p&gt;&amp;nbsp;&lt;/p&gt;".</summary>
    /// <exception cref="ArgumentNullException">subject or calendarMailbox null.</exception>
    public static string Html(DateTime start, DateTime end, string? timeZoneLabel, string subject, string calendarMailbox) =>
        "<p>" + DraftHtml.HighlightOpen + Esc(Text(start, end, timeZoneLabel, subject, calendarMailbox))
        + DraftHtml.HighlightClose + "</p><p>&nbsp;</p>";

    /// <summary>"The meeting was saved, but the note in Drafts couldn't be created: &lt;reason&gt;."</summary>
    public static string Error(string? reason)
    {
        var r = (reason ?? "").Trim().TrimEnd('.').TrimEnd();
        if (r.Length == 0) r = UnexpectedError;
        r = TrayText.Truncate(r, MaxReasonChars);
        return $"The meeting was saved, but the note in Drafts couldn't be created: {r}.";
    }

    /// <summary>McpException (incl. ItemNotFoundException, OutlookUnavailableException) → its Message;
    /// anything else → "an unexpected error (&lt;TypeName&gt;)" (never the message). Null → UnexpectedError.</summary>
    public static string Reason(Exception? ex) => ex switch
    {
        null => UnexpectedError,
        McpException => ex.Message,
        _ => $"{UnexpectedError} ({ex.GetType().Name})",
    };

    private static string CleanSubject(string subject)
    {
        var sb = new StringBuilder(subject.Length);
        var space = false;
        foreach (var c in subject)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return TrayText.Truncate(sb.ToString(), MaxSubjectChars);
    }

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>DraftId = the note's EntryID on success; Error = the note_error text on failure. Exactly one is non-null.</summary>
public sealed record MeetingNoteOutcome(string? DraftId, string? Error);

/// <summary>Checks the reply target and saves the note reply draft (SPEC §22.4). Never sends.</summary>
public sealed class MeetingNotes
{
    private readonly IReplyTargetCheck _targets;
    private readonly IReplyDraftWriter _writer;
    private readonly Func<string?> _signature;
    private readonly ILogger? _logger;

    /// <exception cref="ArgumentNullException">targets, writer or replySignatureHtml null.</exception>
    public MeetingNotes(IReplyTargetCheck targets, IReplyDraftWriter writer, Func<string?> replySignatureHtml,
        ILogger? logger = null)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _signature = replySignatureHtml ?? throw new ArgumentNullException(nameof(replySignatureHtml));
        _logger = logger;
    }

    /// <summary>Delegates to IReplyTargetCheck.CheckAsync(id.Trim(), ct). Exceptions propagate unchanged.</summary>
    /// <exception cref="ArgumentNullException">id null.</exception>
    public Task CheckAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _targets.CheckAsync(id.Trim(), ct);
    }

    /// <summary>The plan SaveAsync hands to the writer. Pure apart from calling replySignatureHtml.</summary>
    /// <exception cref="ArgumentNullException">id, request or timeZoneLabel null.</exception>
    public ReplyPlan Plan(string id, MeetingRequest request, string timeZoneLabel)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeZoneLabel);
        var sig = _signature();
        var html = MeetingNoteText.Html(request.Start, request.End, timeZoneLabel, request.Subject, request.Mailbox);
        return new ReplyPlan(id.Trim(), Mailbox: null, ReplyAll: false,
            DraftBody.Fragment(html, html: true, sig), DraftBody.SignatureContent(sig) is not null,
            DraftMarker.Tool, WorkPriority.Interactive);
    }

    /// <summary>Builds the plan and calls IReplyDraftWriter.ReplyAsync. Never throws (except ArgumentNullException
    /// for null arguments): every failure becomes Outcome(null, MeetingNoteText.Error(MeetingNoteText.Reason(ex)))
    /// plus one Warning log with the exception type only.</summary>
    public async Task<MeetingNoteOutcome> SaveAsync(string id, MeetingRequest request, string timeZoneLabel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeZoneLabel);
        try
        {
            var plan = Plan(id, request, timeZoneLabel);
            var made = await _writer.ReplyAsync(plan, ct).ConfigureAwait(false);
            return new MeetingNoteOutcome(made.EntryId, null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Meeting draft: the note in Drafts couldn't be created ({ExceptionType}).", ex.GetType().Name);
            return new MeetingNoteOutcome(null, MeetingNoteText.Error(MeetingNoteText.Reason(ex)));
        }
    }
}
