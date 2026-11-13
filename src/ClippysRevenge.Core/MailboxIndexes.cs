using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>One search row tagged with the mailbox key it came from ("" = own).</summary>
public sealed record MailboxHit(string Mailbox, EmailSummary Email);

/// <summary>Result of Locate: the mailbox key ("" = own) and the indexed row.</summary>
public sealed record LocatedEmail(string Mailbox, EmailDetail Email);

/// <summary>One EmailIndex per enabled mailbox (SPEC §9.3).</summary>
public sealed class MailboxIndexes : IDisposable
{
    public const string OwnMailbox = "";
    public const string AllMailboxes = "all";
    public const string OwnFileName = "index.db";

    private readonly string _dataDir;
    private readonly Lock _lock = new();
    private readonly List<string> _keys = [];
    private readonly Dictionary<string, EmailIndex> _map = new(StringComparer.Ordinal);
    private bool _disposed;

    public MailboxIndexes(string dataDir, IEnumerable<string> enabledSharedMailboxes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        ArgumentNullException.ThrowIfNull(enabledSharedMailboxes);
        var shared = new List<string>();
        foreach (var s in enabledSharedMailboxes)
        {
            var key = Normalize(s);
            ValidateShared(key);
            if (!shared.Contains(key)) shared.Add(key);
        }
        _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        Open(OwnMailbox);
        foreach (var key in shared) Open(key);
    }

    private EmailIndex Open(string key)
    {
        var ix = new EmailIndex(PathFor(_dataDir, key));
        _keys.Add(key);
        _map[key] = ix;
        return ix;
    }

    private static void ValidateShared(string key)
    {
        if (key.Length == 0 || key == AllMailboxes || !key.Contains('@'))
            throw new ArgumentException($"\"{key}\" is not a valid shared mailbox address.", "mailbox");
    }

    public IReadOnlyList<string> Mailboxes
    {
        get
        {
            lock (_lock) return _keys.ToArray();
        }
    }

    public static string Normalize(string? mailbox) =>
        string.IsNullOrWhiteSpace(mailbox) ? "" : mailbox.Trim().ToLowerInvariant();

    public static string PathFor(string dataDir, string? mailbox)
    {
        var key = Normalize(mailbox);
        if (key == AllMailboxes) throw new ArgumentException("\"all\" has no index file.", nameof(mailbox));
        return key.Length == 0
            ? Path.Combine(dataDir, OwnFileName)
            : Path.Combine(dataDir, "index-" + FileNames.SafeFileName(key) + ".db");
    }

    private static string KeyList(IReadOnlyList<string> keys) =>
        string.Join(", ", keys.Select(k => k.Length == 0 ? "\"\" (your own mailbox)" : "\"" + k + "\""));

    public static McpException Unknown(string? input, IReadOnlyList<string> keys)
    {
        var shown = (input ?? "").Trim();
        if (shown.Length > 100) shown = shown[..100];
        return new McpException(
            $"Mailbox \"{shown}\" is not configured or not enabled. Available mailboxes: {KeyList(keys)}. To use another shared mailbox, ask the user to enable it in the Clippy tray menu under Shared mailboxes.");
    }

    public EmailIndex For(string? mailbox)
    {
        var key = Normalize(mailbox);
        IReadOnlyList<string> keys;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (key != AllMailboxes && _map.TryGetValue(key, out var ix)) return ix;
            keys = _keys.ToArray();
        }
        if (key == AllMailboxes)
            throw new McpException($"Mailbox \"all\" only works with search tools. Use one mailbox: {KeyList(keys)}.");
        throw Unknown(mailbox, keys);
    }

    private (string Key, EmailIndex Index)[] Snapshot()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _keys.Select(k => (k, _map[k])).ToArray();
        }
    }

    public IReadOnlyList<MailboxHit> Search(SearchFilter f, string? mailbox)
    {
        ArgumentNullException.ThrowIfNull(f);
        var key = Normalize(mailbox);
        if (key != AllMailboxes)
            return For(key).Search(f).Select(e => new MailboxHit(key, e)).ToList();

        var limit = f.Limit;
        var offset = Math.Max(f.Offset, 0);
        if (limit <= 0) return [];
        var need = (int)Math.Min((long)offset + limit, int.MaxValue);

        var all = new List<MailboxHit>();
        foreach (var (k, ix) in Snapshot())
        {
            var collected = 0;
            var o = 0;
            while (collected < need)
            {
                var page = Math.Min(200, need - collected);
                var rows = ix.Search(f with { Offset = o, Limit = page });
                foreach (var r in rows) all.Add(new MailboxHit(k, r));
                collected += rows.Count;
                if (rows.Count < page) break;
                o += rows.Count;
            }
        }
        return all.OrderByDescending(h => h.Email.Date).Skip(offset).Take(limit).ToList();
    }

    public LocatedEmail? Locate(string? entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId)) return null;
        foreach (var (k, ix) in Snapshot())
            if (ix.Get(entryId) is { } d) return new LocatedEmail(k, d);
        return null;
    }

    public EmailIndex Add(string mailbox)
    {
        var key = Normalize(mailbox);
        if (key.Length != 0) ValidateShared(key);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_map.TryGetValue(key, out var existing)) return existing;
            return Open(key);
        }
    }

    private static string ValidateRemovable(string mailbox)
    {
        var key = Normalize(mailbox);
        if (key.Length == 0 || key == AllMailboxes)
            throw new ArgumentException("The own mailbox cannot be disabled or removed.", nameof(mailbox));
        return key;
    }

    public bool Disable(string mailbox)
    {
        var key = ValidateRemovable(mailbox);
        EmailIndex? ix;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_map.Remove(key, out ix)) return false;
            _keys.Remove(key);
        }
        ix.Close();
        return true;
    }

    public void Remove(string mailbox, bool deleteFile)
    {
        var key = ValidateRemovable(mailbox);
        Disable(key);
        if (!deleteFile) return;
        var path = PathFor(_dataDir, key);
        File.Delete(path);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
    }

    public void Dispose()
    {
        EmailIndex[] all;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            all = _map.Values.ToArray();
        }
        foreach (var ix in all) ix.Close();
    }
}
