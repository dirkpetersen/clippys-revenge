namespace ClippysRevenge.Core;

/// <summary>One line of the tray's Shared mailboxes submenu (SPEC §6.2).</summary>
public sealed record SharedMenuEntry(string Smtp, string DisplayName, string Label, bool Checked, bool NotInOutlook);

/// <summary>The submenu model: its entries and whether Outlook's mailboxes are unknown right now.</summary>
public sealed record SharedMenuModel(IReadOnlyList<SharedMenuEntry> Entries, bool StartOutlookHint);

/// <summary>Pure model of the Shared mailboxes submenu (SPEC §6.2). No COM, no WinForms.</summary>
public static class SharedMailboxMenu
{
    public const string NotInOutlookSuffix = " (not in Outlook)";
    public const string StartOutlookHintText = "Start Outlook to see more mailboxes";

    /// <summary>
    /// Configured mailboxes first (in settings order), then mounted ones not yet configured.
    /// <paramref name="mounted"/> is null while Outlook is not running or the list is not known:
    /// only the configured mailboxes show, plus the hint line.
    /// </summary>
    public static SharedMenuModel Build(IReadOnlyList<MailboxStore>? mounted, IEnumerable<SharedMailbox> configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        var shared = mounted is null ? null : StoreCatalog.SharedOnly(mounted);
        var entries = new List<SharedMenuEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in configured)
        {
            if (c is null) continue;
            var key = EmailAddress.Normalize(c.Smtp);
            if (key.Length == 0 || !seen.Add(key)) continue;
            var store = shared?.FirstOrDefault(s => s.Smtp == key);
            var name = !string.IsNullOrWhiteSpace(c.DisplayName) ? c.DisplayName : store?.DisplayName ?? "";
            var notIn = shared is not null && store is null;
            entries.Add(new SharedMenuEntry(key, name, Label(name, key) + (notIn ? NotInOutlookSuffix : ""), c.Enabled, notIn));
        }
        if (shared is not null)
            foreach (var s in shared)
            {
                if (!seen.Add(s.Smtp)) continue;
                entries.Add(new SharedMenuEntry(s.Smtp, s.DisplayName, Label(s.DisplayName, s.Smtp), false, false));
            }
        return new SharedMenuModel(entries, mounted is null);
    }

    private static string Label(string name, string smtp) =>
        string.IsNullOrWhiteSpace(name) ? smtp : $"{name} <{smtp}>";
}

/// <summary>What the user chose when unchecking a mailbox.</summary>
public enum StopIndexingChoice { DeleteIndex, KeepIndex, Cancel }

/// <summary>
/// Applies check / uncheck from the tray: settings entry, saved settings, index open/close/delete (SPEC §6.2, §9.3).
/// Unchecking sets <c>Enabled = false</c> and keeps the entry (SPEC §16).
/// </summary>
public sealed class SharedMailboxToggler
{
    private readonly Settings _settings;
    private readonly MailboxIndexes _indexes;
    private readonly Action<Settings> _save;
    private readonly Action _changed;

    public SharedMailboxToggler(Settings settings, MailboxIndexes indexes, Action<Settings> save, Action changed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(changed);
        _settings = settings;
        _indexes = indexes;
        _save = save;
        _changed = changed;
    }

    /// <summary>Adds or enables the mailbox, opens its index and tells sync to start the backfill. Throws on an invalid address.</summary>
    public void Check(string smtp, string displayName)
    {
        var key = EmailAddress.Normalize(smtp);
        _indexes.Add(key); // validates the address before settings change
        var entry = Find(key);
        if (entry is null)
        {
            _settings.SharedMailboxes.Add(new SharedMailbox { Smtp = key, DisplayName = displayName ?? "", Enabled = true });
        }
        else
        {
            entry.Enabled = true;
            if (string.IsNullOrWhiteSpace(entry.DisplayName)) entry.DisplayName = displayName ?? "";
        }
        _save(_settings);
        _changed();
    }

    /// <summary>Disables the mailbox (entry kept), closes its index, and deletes the files when asked.</summary>
    public void Uncheck(string smtp, bool deleteIndex)
    {
        var key = EmailAddress.Normalize(smtp);
        var entry = Find(key);
        if (entry is not null) entry.Enabled = false;
        _save(_settings);
        _indexes.Remove(key, deleteIndex);
        _changed();
    }

    private SharedMailbox? Find(string key) =>
        _settings.SharedMailboxes.FirstOrDefault(m => m is not null && EmailAddress.Normalize(m.Smtp) == key);
}
