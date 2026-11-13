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
