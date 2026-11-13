using System.Diagnostics.CodeAnalysis;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>A store as read from Outlook: ExchangeStoreType is the raw OlExchangeStoreType value.</summary>
public sealed record StoreCandidate(string DisplayName, string StoreId, string? Smtp, int ExchangeStoreType, bool IsDefaultStore);

/// <summary>A mounted Exchange mailbox (SPEC §8). Smtp is lower-cased; "" when unknown.</summary>
public sealed record MailboxStore(string DisplayName, string StoreId, string Smtp, bool IsOwn);

/// <summary>Pure store filtering and error text for OutlookStores (SPEC §6.2, §8).</summary>
public static class StoreCatalog
{
    // OlExchangeStoreType
    public const int PrimaryExchangeMailbox = 0;
    public const int ExchangeMailbox = 1;
    public const int AdditionalExchangeMailbox = 4;

    /// <summary>Only Exchange mailbox stores count; public folders and PSTs do not.</summary>
    public static bool IsMailboxType(int exchangeStoreType) =>
        exchangeStoreType is PrimaryExchangeMailbox or ExchangeMailbox or AdditionalExchangeMailbox;

    /// <summary>Mounted mailboxes in input order. The user's own is the default store (or the primary mailbox type).</summary>
    public static IReadOnlyList<MailboxStore> Filter(IEnumerable<StoreCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var result = new List<MailboxStore>();
        var mailboxes = candidates.Where(c => IsMailboxType(c.ExchangeStoreType)).ToList();
        // The default store is the user's own; only when none qualifies (e.g. a PST default) use the first primary mailbox.
        var ownCandidate = mailboxes.FirstOrDefault(c => c.IsDefaultStore)
            ?? mailboxes.FirstOrDefault(c => c.ExchangeStoreType == PrimaryExchangeMailbox);
        foreach (var c in mailboxes)
        {
            var own = ReferenceEquals(c, ownCandidate);
            result.Add(new MailboxStore(c.DisplayName ?? "", c.StoreId, EmailAddress.Normalize(c.Smtp), own));
        }
        return result;
    }

    /// <summary>Shared mailboxes for the tray submenu: mounted, not own, known SMTP.</summary>
    public static IReadOnlyList<MailboxStore> SharedOnly(IEnumerable<MailboxStore> stores) =>
        stores.Where(s => !s.IsOwn && s.Smtp.Length > 0).ToList();

    /// <summary>The store for a mailbox argument: own for "", else the store with that SMTP. Null when not mounted.</summary>
    public static MailboxStore? Find(IReadOnlyList<MailboxStore> stores, string? mailbox)
    {
        var key = EmailAddress.Normalize(mailbox);
        return key.Length == 0
            ? stores.FirstOrDefault(s => s.IsOwn)
            : stores.FirstOrDefault(s => !s.IsOwn && s.Smtp == key);
    }

    /// <summary>
    /// Error for an address that cannot be served. configuredKeys are the enabled mailbox keys ("" = own).
    /// Configured but not mounted gets its own text; anything else is the §9.3 "not configured" error.
    /// </summary>
    public static McpException NotAvailable(string? input, IReadOnlyList<string> configuredKeys)
    {
        var key = EmailAddress.Normalize(input);
        if (key.Length > 0 && configuredKeys.Contains(key, StringComparer.Ordinal))
            return new McpException(
                $"Mailbox \"{key}\" is configured but is not open in Outlook. Ask the user to add it to their Outlook profile, then try again.");
        return MailboxIndexes.Unknown(input, configuredKeys);
    }
}

/// <summary>
/// Plain-data cache of the store list per Outlook connection (SPEC §8). Holds strings only, never COM objects.
/// Thread-safe: abandoned worker threads may call it while the live one does.
/// </summary>
public sealed class StoreListCache
{
    public static readonly TimeSpan ListTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan TimeoutCooldown = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _attempted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _cooldown = new(StringComparer.Ordinal);
    private List<StoreCandidate>? _candidates;
    private IReadOnlyList<MailboxStore>? _stores;
    private long _connectionId = -1;
    private DateTimeOffset _setAt;

    public StoreListCache(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The cached list for this connection when it is younger than ListTtl, with known SMTPs applied.</summary>
    public bool TryGet(long connectionId, [NotNullWhen(true)] out IReadOnlyList<MailboxStore>? stores)
    {
        lock (_lock)
        {
            if (IsValid(connectionId))
            {
                stores = _stores!;
                return true;
            }
            stores = null;
            return false;
        }
    }

    /// <summary>
    /// Stores the enumerated candidates for this connection and returns StoreCatalog.Filter of them. A candidate
    /// with a null/blank Smtp gets the SMTP remembered for its StoreId by RecordLookup, if any.
    /// </summary>
    public IReadOnlyList<MailboxStore> Set(long connectionId, IReadOnlyList<StoreCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_lock)
        {
            _candidates = [.. candidates.Select(Apply)];
            _stores = StoreCatalog.Filter(_candidates);
            _connectionId = connectionId;
            _setAt = _time.GetUtcNow();
            return _stores;
        }
    }

    /// <summary>
    /// Records a lookup result. A non-blank smtp is remembered for storeId for the life of the process
    /// (normalized with EmailAddress.Normalize) and applied to the cached list. A null/blank smtp marks
    /// storeId as attempted for this connection only.
    /// </summary>
    public void RecordLookup(long connectionId, string storeId, string? smtp)
    {
        ArgumentNullException.ThrowIfNull(storeId);
        var key = EmailAddress.Normalize(smtp);
        lock (_lock)
        {
            if (key.Length == 0)
            {
                _attempted[storeId] = connectionId;
                return;
            }
            _known[storeId] = key;
            if (_candidates is not null)
            {
                _candidates = [.. _candidates.Select(Apply)];
                _stores = StoreCatalog.Filter(_candidates);
            }
        }
    }

    /// <summary>The lookup for storeId timed out: don't offer it again until TimeoutCooldown has passed.</summary>
    public void RecordTimeout(string storeId)
    {
        ArgumentNullException.ThrowIfNull(storeId);
        lock (_lock)
        {
            _cooldown[storeId] = _time.GetUtcNow() + TimeoutCooldown;
        }
    }

    /// <summary>
    /// Store ids that still need an owner lookup on this connection. When includeUnconfigured is false, this
    /// is empty once every key in sharedKeys matches a non-own cached store. Otherwise it contains the cached
    /// non-own stores with an empty Smtp that are neither attempted on this connection nor cooling down,
    /// in list order. Empty when there is no cached list for this connection.
    /// </summary>
    public IReadOnlyList<string> PendingLookups(long connectionId, IReadOnlyList<string> sharedKeys, bool includeUnconfigured)
    {
        ArgumentNullException.ThrowIfNull(sharedKeys);
        lock (_lock)
        {
            return Pending(connectionId, sharedKeys, includeUnconfigured);
        }
    }

    /// <summary>True when there is a valid list for this connection and PendingLookups(...) is empty.</summary>
    public bool IsSettled(long connectionId, IReadOnlyList<string> sharedKeys, bool includeUnconfigured)
    {
        ArgumentNullException.ThrowIfNull(sharedKeys);
        lock (_lock)
        {
            return IsValid(connectionId) && Pending(connectionId, sharedKeys, includeUnconfigured).Count == 0;
        }
    }

    /// <summary>Drops the cached list (known SMTPs and cooldowns are kept).</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _candidates = null;
            _stores = null;
            _connectionId = -1;
        }
    }

    private bool IsValid(long connectionId) =>
        _stores is not null && _connectionId == connectionId && _time.GetUtcNow() - _setAt <= ListTtl;

    private StoreCandidate Apply(StoreCandidate c) =>
        string.IsNullOrWhiteSpace(c.Smtp) && _known.TryGetValue(c.StoreId, out var k) ? c with { Smtp = k } : c;

    private IReadOnlyList<string> Pending(long connectionId, IReadOnlyList<string> sharedKeys, bool includeUnconfigured)
    {
        if (!IsValid(connectionId))
        {
            return [];
        }
        var stores = _stores!;
        if (!includeUnconfigured)
        {
            var matched = sharedKeys.All(k =>
            {
                var key = EmailAddress.Normalize(k);
                return key.Length == 0 || stores.Any(s => !s.IsOwn && s.Smtp == key);
            });
            if (matched)
            {
                return [];
            }
        }
        var now = _time.GetUtcNow();
        var result = new List<string>();
        foreach (var s in stores)
        {
            if (s.IsOwn || s.Smtp.Length > 0)
            {
                continue;
            }
            if (_attempted.TryGetValue(s.StoreId, out var conn) && conn == connectionId)
            {
                continue;
            }
            if (_cooldown.TryGetValue(s.StoreId, out var until) && now < until)
            {
                continue;
            }
            result.Add(s.StoreId);
        }
        return result;
    }
}
