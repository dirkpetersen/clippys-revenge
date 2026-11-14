namespace ClippysRevenge.Core.Tests;

/// <summary>Scripted IFreeBusySource. Records every free/busy call; GetCurrentUserAsync always throws (drafting must not call it).</summary>
internal sealed class FakeFreeBusySource : IFreeBusySource
{
    private readonly Lock _lock = new();
    private readonly List<(string Email, DateTime From, DateTime To, int Interval, WorkPriority Priority)> _calls = [];

    /// <summary>email (OrdinalIgnoreCase) to runs; every other slot is '0'. Missing email gives null (unknown).</summary>
    public Dictionary<string, List<(DateTime Start, DateTime End, char Code)>> Calendars { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>email to the exact string to return (e.g. "x", "000"); wins over Calendars.</summary>
    public Dictionary<string, string> Raw { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>email to the exception to throw.</summary>
    public Dictionary<string, Exception> Throws { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs on every call before anything else (test hook, e.g. to cancel a token).</summary>
    public Action<string>? OnCall { get; set; }

    public int CurrentUserCalls { get; private set; }

    public IReadOnlyList<(string Email, DateTime From, DateTime To, int Interval, WorkPriority Priority)> Calls
    {
        get { lock (_lock) return _calls.ToArray(); }
    }

    /// <summary>Known attendees with nothing busy.</summary>
    public void Free(params string[] emails)
    {
        foreach (var e in emails) Calendars[e] = [];
    }

    public Task<Recipient> GetCurrentUserAsync(WorkPriority priority, CancellationToken ct)
    {
        lock (_lock) CurrentUserCalls++;
        throw new InvalidOperationException("GetCurrentUserAsync must not be called by drafting.");
    }

    public Task<string?> GetFreeBusyAsync(string email, DateTime from, DateTime to, int intervalMinutes,
        WorkPriority priority, CancellationToken ct)
    {
        lock (_lock) _calls.Add((email, from, to, intervalMinutes, priority));
        OnCall?.Invoke(email);
        if (Throws.TryGetValue(email, out var ex)) throw ex;
        if (Raw.TryGetValue(email, out var raw)) return Task.FromResult<string?>(raw);
        if (!Calendars.TryGetValue(email, out var runs)) return Task.FromResult<string?>(null);

        var start = from.Date;
        var n = (int)Math.Ceiling((to - start).TotalMinutes / intervalMinutes);
        var chars = new char[n];
        for (var i = 0; i < n; i++)
        {
            var s = start.AddMinutes((double)i * intervalMinutes);
            var e = s.AddMinutes(intervalMinutes);
            var code = '0';
            foreach (var r in runs)
                if (r.Start < e && s < r.End) code = r.Code;
            chars[i] = code;
        }
        return Task.FromResult<string?>(new string(chars));
    }
}
