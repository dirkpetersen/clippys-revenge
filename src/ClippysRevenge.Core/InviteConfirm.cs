using System.Globalization;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>One invite recipient as shown in the dialog.</summary>
public sealed record InviteAttendee(string Name, string Email, bool External);

/// <summary>What send_meeting_invite wants to send. Start/End are local wall-clock times.</summary>
public sealed record InviteConfirmRequest(
    string Organizer,
    bool OrganizerIsShared,
    string Subject,
    DateTime Start,
    DateTime End,
    string? Location,
    bool Teams,
    IReadOnlyList<InviteAttendee> Recipients,
    string? Body);

public static class InviteAttendees
{
    /// <summary>
    /// True when the recipient must be highlighted as outside the user's organization.
    /// Never throws; any input it can't classify is external.
    /// </summary>
    public static bool IsExternal(string? email, bool isExchangeEntry, string? organizerSmtp)
    {
        if (isExchangeEntry)
        {
            return false;
        }
        var d = DomainOf(email);
        var o = DomainOf(organizerSmtp);
        if (d is null || o is null)
        {
            return true;
        }
        if (string.Equals(d, o, StringComparison.OrdinalIgnoreCase)
            || d.EndsWith("." + o, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private static string? DomainOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }
        var at = address.IndexOf('@');
        if (at < 0 || address.IndexOf('@', at + 1) >= 0)
        {
            return null;
        }
        var domain = address[(at + 1)..].Trim();
        return domain.Length == 0 ? null : domain;
    }
}

public static class InviteConfirmTexts
{
    public const string Title = "Clippy's Revenge — confirm meeting invite";
    public const string Heading = "Claude wants to send this meeting invite. Nothing is sent until you click Send.";
    public const string SendButton = "Send";
    public const string SaveDraftButton = "Save as draft";
    public const string CancelButton = "Cancel";
    public const string ExternalSuffix = " — outside your organization";
    public const string NoLocation = "(none)";
    public const string NoSubject = "(no subject)";
    public const string NoBody = "(no message body)";
    public const string SavingNow = "Time is up — saving as a draft…";
    public const string ShutdownMessage =
        "Clippy's Revenge is shutting down, so the invite was not created. Try again after it restarts.";
    public const string DialogFailedMessage =
        "The confirmation dialog could not be shown, so the invite was not created. Ask the user to check the Clippy's Revenge tray icon, then try again.";

    /// <summary>"Saved as draft in m:ss unless you choose." Remaining is rounded up to whole seconds, clamped at 0.</summary>
    public static string Countdown(TimeSpan remaining)
    {
        var s = (long)Math.Max(0, Math.Ceiling(remaining.TotalSeconds));
        return string.Create(CultureInfo.InvariantCulture, $"Saved as draft in {s / 60}:{s % 60:00} unless you choose.");
    }
}

public sealed record InviteAttendeeLine(string Text, bool External);

public sealed class InviteConfirmView
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private InviteConfirmView()
    {
    }

    public string Title { get; private init; } = InviteConfirmTexts.Title;
    public string Heading { get; private init; } = InviteConfirmTexts.Heading;
    public string OrganizerLine { get; private init; } = "";
    public string Subject { get; private init; } = "";
    public string? WhenShort { get; private init; }
    public string WhenFull { get; private init; } = "";
    public string LocationLine { get; private init; } = "";
    public string TeamsLine { get; private init; } = "";
    public IReadOnlyList<InviteAttendeeLine> Recipients { get; private init; } = [];
    public string RecipientsHeader { get; private init; } = "";
    public string? ExternalSummary { get; private init; }
    public string Body { get; private init; } = "";

    public static InviteConfirmView Create(InviteConfirmRequest request, string? timeZoneLabel,
                                           int workStartHour, int workEndHour)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Organizer, nameof(request.Organizer));
        ArgumentNullException.ThrowIfNull(request.Subject, nameof(request.Subject));
        ArgumentNullException.ThrowIfNull(request.Recipients, nameof(request.Recipients));
        foreach (var r in request.Recipients)
        {
            ArgumentNullException.ThrowIfNull(r, nameof(request.Recipients));
            ArgumentNullException.ThrowIfNull(r.Name, nameof(request.Recipients));
            ArgumentNullException.ThrowIfNull(r.Email, nameof(request.Recipients));
        }
        if (request.End <= request.Start)
        {
            throw new ArgumentException("Meeting end must be after its start.", nameof(request));
        }
        if (request.Recipients.Count == 0)
        {
            throw new ArgumentException("An invite needs at least one recipient.", nameof(request));
        }

        string? whenShort;
        try
        {
            var formatted = SlotFormatter.Format(1, request.Start, request.End, timeZoneLabel, workStartHour, workEndHour);
            whenShort = formatted.StartsWith("1. ", StringComparison.Ordinal) ? formatted[3..] : formatted;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            whenShort = null;
        }

        const string full = "dddd, MMMM d, yyyy, h:mm tt";
        var whenFull = request.Start.Date == request.End.Date
            ? request.Start.ToString(full, Inv) + " – " + request.End.ToString("h:mm tt", Inv)
            : request.Start.ToString(full, Inv) + " – " + request.End.ToString(full, Inv);
        if (!string.IsNullOrWhiteSpace(timeZoneLabel))
        {
            whenFull += " " + timeZoneLabel.Trim();
        }

        var lines = new List<InviteAttendeeLine>(request.Recipients.Count);
        var external = 0;
        foreach (var r in request.Recipients)
        {
            var text = Core.Recipients.Format([new Recipient(r.Name, r.Email)]);
            if (r.External)
            {
                text += InviteConfirmTexts.ExternalSuffix;
                external++;
            }
            lines.Add(new InviteAttendeeLine(text, r.External));
        }

        var subject = request.Subject.Trim();
        var location = string.IsNullOrWhiteSpace(request.Location)
            ? InviteConfirmTexts.NoLocation
            : request.Location.Trim();

        return new InviteConfirmView
        {
            OrganizerLine = "From: " + request.Organizer.Trim() + (request.OrganizerIsShared ? " (shared mailbox)" : ""),
            Subject = subject.Length == 0 ? InviteConfirmTexts.NoSubject : subject,
            WhenShort = whenShort,
            WhenFull = whenFull,
            LocationLine = "Location: " + location,
            TeamsLine = request.Teams ? "Teams meeting: yes" : "Teams meeting: no",
            Recipients = lines,
            RecipientsHeader = string.Create(Inv, $"To ({lines.Count}):"),
            ExternalSummary = external switch
            {
                0 => null,
                1 => "1 recipient is outside your organization.",
                _ => string.Create(Inv, $"{external} recipients are outside your organization."),
            },
            Body = string.IsNullOrWhiteSpace(request.Body)
                ? InviteConfirmTexts.NoBody
                : request.Body.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n"),
        };
    }
}

/// <summary>Raw result for InviteGate.Decide(mode, Choice, TimedOut).</summary>
public readonly record struct InviteConfirmOutcome(InviteChoice? Choice, bool TimedOut);

/// <summary>Implemented by the App on the UI thread. Called by the queue from any thread.</summary>
public interface IInviteConfirmPresenter
{
    /// <summary>
    /// Shows one dialog and completes when it has closed: with the button the user
    /// clicked, or null when it closed any other way. When <paramref name="dismiss"/> is
    /// signalled the presenter must close the dialog and then complete (any value).
    /// Must never complete before the window is closed (or was never shown).
    /// </summary>
    Task<InviteChoice?> ShowAsync(InviteConfirmView view, DateTimeOffset deadline, CancellationToken dismiss);
}

/// <summary>FIFO queue showing one confirmation dialog at a time (SPEC §6.6).</summary>
public sealed class InviteConfirmQueue : IDisposable
{
    private sealed class Entry(InviteConfirmView view, TimeSpan timeout, CancellationToken ct)
    {
        public InviteConfirmView View { get; } = view;
        public TimeSpan Timeout { get; } = timeout;
        public CancellationToken Ct { get; } = ct;
        public TaskCompletionSource<InviteConfirmOutcome> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Dismiss { get; } = new();
        public LinkedListNode<Entry>? Node { get; set; }
        public CancellationTokenRegistration Reg { get; set; }
        public bool Resolved { get; set; }
        public bool PresenterDone { get; set; }
        public bool Cancelled { get; set; }
        public bool TimerFired { get; set; }
        public ITimer? Timer;
        public IDisposable? Scope;
    }

    private readonly IInviteConfirmPresenter _presenter;
    private readonly ActivityHub _hub;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly LinkedList<Entry> _queue = new();
    private Entry? _current;
    private bool _disposed;

    public InviteConfirmQueue(IInviteConfirmPresenter presenter, ActivityHub hub, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(hub);
        _presenter = presenter;
        _hub = hub;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Requests waiting plus the one being shown.</summary>
    public int Pending
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count + (_current is null ? 0 : 1);
            }
        }
    }

    /// <summary>Queues the dialog, waits for its turn, shows it, and returns the user's answer.</summary>
    public Task<InviteConfirmOutcome> ConfirmAsync(InviteConfirmView view, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var entry = new Entry(view, timeout, ct);
        lock (_lock)
        {
            if (_disposed)
            {
                return Task.FromException<InviteConfirmOutcome>(new McpException(InviteConfirmTexts.ShutdownMessage));
            }
            if (ct.IsCancellationRequested)
            {
                return Task.FromCanceled<InviteConfirmOutcome>(ct);
            }
            entry.Node = _queue.AddLast(entry);
        }
        if (ct.CanBeCanceled)
        {
            entry.Reg = ct.Register(() => OnCancel(entry));
            if (entry.Tcs.Task.IsCompleted)
            {
                entry.Reg.Dispose();
            }
        }
        TryStartNext();
        return entry.Tcs.Task;
    }

    private void OnCancel(Entry e)
    {
        var dismiss = false;
        var removed = false;
        lock (_lock)
        {
            if (e.Resolved)
            {
                return;
            }
            if (e.Node is not null)
            {
                _queue.Remove(e.Node);
                e.Node = null;
                e.Resolved = true;
                removed = true;
            }
            else if (ReferenceEquals(_current, e) && !e.PresenterDone)
            {
                e.Cancelled = true;
                dismiss = true;
            }
        }
        if (removed)
        {
            e.Tcs.TrySetCanceled(e.Ct);
            e.Reg.Dispose();
        }
        else if (dismiss)
        {
            e.Dismiss.Cancel();
        }
    }

    private void OnTimer(Entry e)
    {
        lock (_lock)
        {
            if (e.Resolved || e.PresenterDone || e.Cancelled || e.TimerFired)
            {
                return;
            }
            e.TimerFired = true;
        }
        e.Dismiss.Cancel();
    }

    private void TryStartNext()
    {
        Entry? e;
        lock (_lock)
        {
            if (_disposed || _current is not null || _queue.First is null)
            {
                return;
            }
            e = _queue.First.Value;
            _queue.RemoveFirst();
            e.Node = null;
            _current = e;
        }
        _ = RunAsync(e);
    }

    private async Task RunAsync(Entry e)
    {
        InviteChoice? choice = null;
        Exception? fault = null;
        DateTimeOffset deadline = default;
        try
        {
            e.Scope = _hub.Begin(ActivityState.AwaitingConfirmation, "");
            deadline = _time.GetUtcNow() + e.Timeout;
            e.Timer = _time.CreateTimer(_ => OnTimer(e), null, e.Timeout, Timeout.InfiniteTimeSpan);
            bool disposed;
            lock (_lock)
            {
                disposed = e.Resolved;
            }
            if (!disposed)
            {
                choice = await _presenter.ShowAsync(e.View, deadline, e.Dismiss.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            fault = ex;
        }
        finally
        {
            Cleanup(e);
        }

        Action? complete = null;
        lock (_lock)
        {
            e.PresenterDone = true;
            if (!e.Resolved)
            {
                e.Resolved = true;
                if (e.Cancelled)
                {
                    complete = () => e.Tcs.TrySetCanceled(e.Ct);
                }
                else if (e.TimerFired)
                {
                    complete = () => e.Tcs.TrySetResult(new InviteConfirmOutcome(null, true));
                }
                else if (fault is not null)
                {
                    var f = fault;
                    complete = () => e.Tcs.TrySetException(new McpException(InviteConfirmTexts.DialogFailedMessage, f));
                }
                else
                {
                    var timedOut = _time.GetUtcNow() >= deadline;
                    var c = choice;
                    complete = () => e.Tcs.TrySetResult(new InviteConfirmOutcome(c, timedOut));
                }
            }
            if (ReferenceEquals(_current, e))
            {
                _current = null;
            }
        }
        complete?.Invoke();
        e.Reg.Dispose();
        TryStartNext();
    }

    private static void Cleanup(Entry e)
    {
        Interlocked.Exchange(ref e.Timer, null)?.Dispose();
        Interlocked.Exchange(ref e.Scope, null)?.Dispose();
    }

    public void Dispose()
    {
        List<Entry> queued;
        Entry? current;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            queued = [.. _queue];
            _queue.Clear();
            current = _current;
            _current = null;
            foreach (var q in queued)
            {
                q.Node = null;
                q.Resolved = true;
            }
            if (current is not null)
            {
                current.Resolved = true;
            }
        }
        foreach (var q in queued)
        {
            q.Tcs.TrySetException(new McpException(InviteConfirmTexts.ShutdownMessage));
            q.Reg.Dispose();
        }
        if (current is not null)
        {
            try
            {
                current.Dismiss.Cancel();
            }
            finally
            {
                Cleanup(current);
                current.Tcs.TrySetException(new McpException(InviteConfirmTexts.ShutdownMessage));
                current.Reg.Dispose();
            }
        }
    }
}
