namespace ClippysRevenge.Core.Tests;

internal sealed record FakeMail(string EntryId, DateTimeOffset? LastModified, DateTimeOffset EventTime, bool Unread, string Subject);

internal sealed record FakeCall(string Method, object?[] Args);

internal sealed class FakeFolder(string key)
{
    public string Key { get; } = key;
    public List<FakeMail> Mails { get; } = [];
}

internal sealed class FakeMailbox(string smtp)
{
    public bool Mounted { get; set; } = true;
    public string Smtp { get; set; } = smtp;
    public string StoreId { get; set; } = "store-" + smtp;
    public Dictionary<string, FakeFolder> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>In-memory IMailSource. Filters work exactly as the interface documents.</summary>
internal sealed class FakeMailSource : IMailSource
{
    private readonly Lock _lock = new();
    private readonly List<FakeCall> _calls = [];
    private readonly List<int> _readBatches = [];
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly List<(string Method, int N, Exception Ex)> _throws = [];

    public Dictionary<string, FakeMailbox> Boxes { get; } = new(StringComparer.Ordinal);
    public bool Sloppy { get; set; }
    public Func<string, CancellationToken, Task>? BeforeCall { get; set; }

    public IReadOnlyList<FakeCall> Calls
    {
        get { lock (_lock) return _calls.ToArray(); }
    }

    public IReadOnlyList<int> ReadBatches
    {
        get { lock (_lock) return _readBatches.ToArray(); }
    }

    public IReadOnlyList<FakeCall> CallsOf(string method) => Calls.Where(c => c.Method == method).ToList();

    public void ThrowOn(string method, int n, Exception ex)
    {
        lock (_lock) _throws.Add((method, n, ex));
    }

    public FakeMailbox Box(string key = "")
    {
        if (!Boxes.TryGetValue(key, out var b)) Boxes[key] = b = new FakeMailbox(key);
        return b;
    }

    public FakeFolder Folder(string key, string path)
    {
        var box = Box(key);
        if (!box.Folders.TryGetValue(path, out var f)) box.Folders[path] = f = new FakeFolder($"fk:{key}:{path}");
        return f;
    }

    public string FolderKey(string key, string path) => Folder(key, path).Key;

    public void Put(string key, string path, FakeMail mail)
    {
        var f = Folder(key, path);
        lock (_lock)
        {
            f.Mails.RemoveAll(m => m.EntryId == mail.EntryId);
            f.Mails.Add(mail);
        }
    }

    private async Task Enter(string method, object?[] args, CancellationToken ct)
    {
        Exception? ex = null;
        lock (_lock)
        {
            _calls.Add(new FakeCall(method, args));
            _counts[method] = _counts.GetValueOrDefault(method) + 1;
            foreach (var t in _throws)
                if (t.Method == method && t.N == _counts[method]) ex = t.Ex;
        }
        if (ex is not null) throw ex;
        var hook = BeforeCall;
        if (hook is not null) await hook(method, ct).ConfigureAwait(false);
    }

    private FakeFolder Lookup(SourceFolder folder)
    {
        foreach (var box in Boxes.Values)
            foreach (var f in box.Folders.Values)
                if (f.Key == folder.FolderKey) return f;
        throw new InvalidOperationException("unknown folder " + folder.FolderKey);
    }

    private static MailRow ToRow(FakeMail m) => new(m.EntryId, m.LastModified, m.EventTime, m.Unread);

    public async Task<SourceMailbox?> OpenMailboxAsync(string mailbox, CancellationToken ct)
    {
        await Enter(nameof(OpenMailboxAsync), [mailbox], ct);
        if (!Boxes.TryGetValue(mailbox, out var box) || !box.Mounted) return null;
        return new SourceMailbox(mailbox, box.Smtp, box.StoreId, "box " + mailbox);
    }

    public async Task<SourceFolder?> GetFolderAsync(SourceMailbox mailbox, string folderPath, CancellationToken ct)
    {
        await Enter(nameof(GetFolderAsync), [folderPath], ct);
        if (!Boxes.TryGetValue(mailbox.Key, out var box) || !box.Folders.TryGetValue(folderPath, out var f)) return null;
        return new SourceFolder(f.Key, folderPath, box.StoreId, FolderPaths.IsUnderSentItems(folderPath));
    }

    public async Task<IReadOnlyList<MailRow>> ListChangedAsync(SourceFolder folder, DateTimeOffset modifiedSince, DateTimeOffset cutoff, CancellationToken ct)
    {
        await Enter(nameof(ListChangedAsync), [folder.Path, modifiedSince, cutoff], ct);
        var f = Lookup(folder);
        lock (_lock)
        {
            return f.Mails
                .Where(m => Sloppy || ((m.LastModified ?? m.EventTime) >= modifiedSince && m.EventTime >= cutoff))
                .Select(ToRow).ToList();
        }
    }

    public async Task<IReadOnlyList<MailRow>> ListRangeAsync(SourceFolder folder, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await Enter(nameof(ListRangeAsync), [folder.Path, from, to], ct);
        var f = Lookup(folder);
        lock (_lock)
        {
            return f.Mails.Where(m => m.EventTime >= from && m.EventTime <= to).Select(ToRow).ToList();
        }
    }

    public async Task<IReadOnlyList<IndexedEmail>> ReadAsync(SourceFolder folder, IReadOnlyList<string> entryIds, CancellationToken ct)
    {
        lock (_lock) _readBatches.Add(entryIds.Count);
        await Enter(nameof(ReadAsync), [folder.Path, entryIds.ToArray()], ct);
        var f = Lookup(folder);
        lock (_lock)
        {
            return entryIds
                .Select(id => f.Mails.FirstOrDefault(m => m.EntryId == id))
                .Where(m => m is not null)
                .Select(m => new IndexedEmail
                {
                    EntryId = m!.EntryId,
                    Folder = "WRONG",
                    StoreId = folder.StoreId,
                    Subject = m.Subject,
                    Body = "body of " + m.EntryId,
                    EventTime = m.EventTime,
                    LastModified = m.LastModified,
                    Unread = m.Unread,
                })
                .ToList();
        }
    }
}
