using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

public enum SettingsTab { Connection, Index, Scheduling, General }

/// <summary>One entry of the Index tab's mailbox picker. Key "" = own mailbox.</summary>
public sealed record SettingsMailboxChoice(string Key, string Label);

public sealed record SettingsChanges(
    bool Any,
    bool PortChanged,
    bool TokenChanged,
    bool StartWithWindowsChanged,
    bool RetentionChanged,
    bool SyncPlanChanged,
    IReadOnlyList<string> EnabledMailboxes,
    IReadOnlyList<string> DisabledMailboxes,
    IReadOnlyList<string> DeleteIndexes)
{
    public bool BreaksClaudeConfig => PortChanged || TokenChanged;
}

/// <summary>The form's staged state. Not thread-safe; UI thread only. Never exposes the pending token via ToString.</summary>
public sealed class SettingsEditor
{
    public const string OwnMailboxLabel = "My mailbox";
    public const string WholeNumberMessage = "Must be a whole number.";

    private readonly Dictionary<string, string> _parseErrors = new(StringComparer.Ordinal);
    private readonly List<string> _parseOrder = [];
    private readonly HashSet<string> _deletes = new(StringComparer.Ordinal);

    /// <exception cref="ArgumentNullException">live is null.</exception>
    public SettingsEditor(Settings live)
    {
        ArgumentNullException.ThrowIfNull(live);
        Draft = live.Clone();
        _base = live.Clone();
    }

    private Settings _base;

    /// <summary>
    /// Three-way merge of shared mailbox changes made elsewhere (tray) since this editor was created or
    /// last rebased: entries added or toggled in <paramref name="live"/> are adopted by the draft unless
    /// the draft itself changed them. Then the baseline becomes live. Call under live.Gate.
    /// </summary>
    public void Rebase(Settings live)
    {
        ArgumentNullException.ThrowIfNull(live);
        foreach (var lm in live.SharedMailboxes ?? [])
        {
            if (lm is null) continue;
            var bm = _base.FindSharedMailbox(lm.Smtp);
            var dm = Draft.FindSharedMailbox(lm.Smtp);
            if (bm is null)
            {
                if (dm is null)
                    Draft.SharedMailboxes.Add(new SharedMailbox
                    {
                        Smtp = lm.Smtp, DisplayName = lm.DisplayName, Enabled = lm.Enabled,
                        RetentionMonths = lm.RetentionMonths, IndexedFolders = [.. lm.IndexedFolders ?? []],
                        AiDrafts = lm.AiDrafts,
                    });
                continue;
            }
            if (dm is null) continue;
            if (lm.Enabled != bm.Enabled && dm.Enabled == bm.Enabled) dm.Enabled = lm.Enabled;
            if (lm.DisplayName != bm.DisplayName && dm.DisplayName == bm.DisplayName) dm.DisplayName = lm.DisplayName;
        }
        _base = live.Clone();
    }

    public Settings Draft { get; }

    /// <summary>Null until RegenerateToken. Raw token; never log it.</summary>
    public string? PendingToken { get; private set; }

    public IReadOnlyCollection<string> PendingIndexDeletes => _deletes;

    public override string ToString() => "SettingsEditor";

    public void RegenerateToken() => PendingToken = AuthToken.Generate();

    /// <summary>
    /// Parses text with int.TryParse(NumberStyles.Integer, InvariantCulture) after Trim.
    /// Success: assign(value), clears the parse error for field, returns true.
    /// Failure: records (field, WholeNumberMessage), leaves Draft unchanged, returns false.
    /// </summary>
    public bool TrySetInt(string field, string? text, Action<int> assign)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(assign);
        if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            assign(v);
            if (_parseErrors.Remove(field)) _parseOrder.Remove(field);
            return true;
        }
        if (!_parseErrors.ContainsKey(field)) _parseOrder.Add(field);
        _parseErrors[field] = WholeNumberMessage;
        return false;
    }

    /// <summary>Draft.AddOrEnableSharedMailbox; also removes the key from PendingIndexDeletes.</summary>
    /// <exception cref="ArgumentException">smtp is not a valid address (from Settings).</exception>
    public SharedMailbox AddMailbox(string smtp, string? displayName)
    {
        var sm = Draft.AddOrEnableSharedMailbox(smtp, displayName);
        _deletes.Remove(EmailAddress.Normalize(smtp));
        return sm;
    }

    /// <summary>Draft.DisableSharedMailbox; when deleteIndex, adds the key to PendingIndexDeletes,
    /// otherwise removes it from there. False when the mailbox isn't in Draft.</summary>
    public bool RemoveMailbox(string smtp, bool deleteIndex)
    {
        if (!Draft.DisableSharedMailbox(smtp)) return false;
        var key = EmailAddress.Normalize(smtp);
        if (deleteIndex) _deletes.Add(key);
        else _deletes.Remove(key);
        return true;
    }

    /// <summary>"My mailbox" first, then every enabled shared mailbox of Draft in list order, Label = MenuLabel.</summary>
    public IReadOnlyList<SettingsMailboxChoice> MailboxChoices()
    {
        var list = new List<SettingsMailboxChoice> { new("", OwnMailboxLabel) };
        if (Draft.SharedMailboxes is not null)
            foreach (var sm in Draft.SharedMailboxes)
                if (sm is { Enabled: true })
                    list.Add(new SettingsMailboxChoice(EmailAddress.Normalize(sm.Smtp), sm.MenuLabel));
        return list;
    }

    /// <summary>Parse errors first, then Draft.Validate() errors whose Field has no parse error. Never throws.</summary>
    public IReadOnlyList<SettingsError> Errors()
    {
        var list = new List<SettingsError>();
        foreach (var f in _parseOrder) list.Add(new SettingsError(f, _parseErrors[f]));
        foreach (var e in Draft.Validate())
            if (!_parseErrors.ContainsKey(e.Field)) list.Add(e);
        return list;
    }

    /// <summary>Diff of Draft (+ pending token/deletes) against live. Never throws.</summary>
    public SettingsChanges Changes(Settings live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var liveEnabled = EnabledKeys(live);
        var draftEnabled = EnabledKeys(Draft);
        var enabled = draftEnabled.Where(k => !liveEnabled.Contains(k)).ToList();
        var disabled = liveEnabled.Where(k => !draftEnabled.Contains(k)).ToList();
        var deletes = disabled.Where(_deletes.Contains).ToList();

        bool retention = Draft.RetentionMonths != live.RetentionMonths;
        bool folders = !SameFolders(live, Draft, "");
        foreach (var d in Draft.SharedMailboxes ?? [])
        {
            if (d is null) continue;
            var l = live.FindSharedMailbox(d.Smtp);
            if (l is null) continue;
            if (l.RetentionMonths != d.RetentionMonths) retention = true;
            if (!SameFolders(live, Draft, EmailAddress.Normalize(d.Smtp))) folders = true;
        }
        bool token = PendingToken is not null;
        bool plan = Draft.SyncIntervalSeconds != live.SyncIntervalSeconds || folders || enabled.Count > 0 || disabled.Count > 0;
        bool any = Draft.ToJson() != live.ToJson() || token || deletes.Count > 0;
        return new SettingsChanges(any, Draft.Port != live.Port, token, Draft.StartWithWindows != live.StartWithWindows,
            retention, plan, enabled, disabled, deletes);
    }

    private static List<string> EnabledKeys(Settings s)
    {
        var keys = new List<string>();
        foreach (var sm in s.SharedMailboxes ?? [])
        {
            if (sm is not { Enabled: true }) continue;
            var k = EmailAddress.Normalize(sm.Smtp);
            if (k.Length > 0 && !keys.Contains(k, StringComparer.Ordinal)) keys.Add(k);
        }
        return keys;
    }

    private static bool SameFolders(Settings a, Settings b, string key)
    {
        var x = new HashSet<string>(MailFolderPath.IndexedFoldersFor(a, key), StringComparer.OrdinalIgnoreCase);
        return x.SetEquals(MailFolderPath.IndexedFoldersFor(b, key));
    }

    /// <summary>Tab for an error field.</summary>
    public static SettingsTab TabFor(string field)
    {
        field ??= "";
        if (field.StartsWith("Port", StringComparison.Ordinal)) return SettingsTab.Connection;
        string[] index = ["RetentionMonths", "SyncIntervalSeconds", "IndexedFolders", "SharedMailboxes"];
        string[] sched = ["WorkStartHour", "WorkEndHour", "BusinessDaysOnly", "TimeZoneLabel", "TentativeIsBusy",
            "MeetingInviteMode", "InviteConfirmTimeoutSeconds"];
        if (index.Any(p => field.StartsWith(p, StringComparison.Ordinal))) return SettingsTab.Index;
        if (sched.Any(p => field.StartsWith(p, StringComparison.Ordinal))) return SettingsTab.Scheduling;
        return SettingsTab.General;
    }

    /// <summary>"SharedMailboxes[3].Smtp" -> 3; anything else -> null.</summary>
    public static int? SharedMailboxIndexFor(string field)
    {
        const string prefix = "SharedMailboxes[";
        if (field is null || !field.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var end = field.IndexOf(']', prefix.Length);
        if (end < 0) return null;
        return int.TryParse(field.AsSpan(prefix.Length, end - prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var i)
            ? i : null;
    }
}

public static class SettingsTexts
{
    public const string RegenerateQuestion =
        "Regenerate the auth token?\n\nThe current token stops working when you press OK or Apply. Claude Code must be set up again with the new \"claude mcp add\" command.";

    public static string PortChangeQuestion(int oldPort, int newPort) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Change the port from {oldPort} to {newPort}?\n\nClaude Code must be set up again with the new \"claude mcp add\" command.");

    public static string StopIndexingQuestion(string label) => $"Stop indexing {label}?";
    public const string DeleteIndexButton = "Delete its index";
    public const string KeepIndexButton = "Keep index file";
    public static string RebuildQuestion(string label) => $"Rebuild the index for {label}?";
    public const string StartOutlookForMailboxes = "Start Outlook to see more mailboxes";
    public const string NoOtherMailboxes = "No other mailboxes are open in Outlook";
    public const string LoadingFolders = "Loading folders…";
    public const string FoldersUnavailable = "Outlook didn't answer, so only the configured folders are shown.";
    public const string NotFoundSuffix = " (not found)";

    public static string TimeZoneAutomatic(string derived) => $"Automatic: {derived}";

    public static string MaskedMcpAddCommand(int port) =>
        TrayText.McpAddCommand(port, "x").Replace("Bearer x\"", "Bearer ••••••••\"", StringComparison.Ordinal);

    public static string WslHelp(int port) =>
        "[wsl2]\r\nnetworkingMode=mirrored\r\n\r\nPut this in %USERPROFILE%\\.wslconfig, then run wsl --shutdown.\r\n"
        + string.Create(CultureInfo.InvariantCulture,
            $"Check from WSL: curl -i -X POST http://127.0.0.1:{port}/mcp should return 401.");

    public static readonly IReadOnlyList<LogLevel> LogLevels =
    [
        LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning,
        LogLevel.Error, LogLevel.Critical, LogLevel.None,
    ];
}

public static class IndexStatsText
{
    public const string NotIndexed = "Not indexed yet.";

    /// <summary>"48,210 emails · oldest 2025-11-12 · newest 2026-11-12 · 512.4 MB · last sync 2 min ago".</summary>
    public static string Format(IndexStatus? status, DateTimeOffset now, TimeZoneInfo tz)
    {
        if (status is null) return NotIndexed;
        string Date(DateTimeOffset? d) =>
            d is null ? "—" : TimeZoneInfo.ConvertTime(d.Value, tz).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sync = status.LastSync is { } ls ? TrayText.RelativePast(ls, now) : "never";
        var count = status.TotalEmails.ToString("N0", CultureInfo.InvariantCulture);
        return $"{count} emails · oldest {Date(status.Oldest)} · newest {Date(status.Newest)} · {Bytes(status.DbSizeBytes)} · last sync {sync}";
    }

    /// <summary>Invariant: under 1024 gives "512 B", then "1.0 KB", "512.4 MB", "1.2 GB" (one decimal, 1024 base).</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, bytes)} B");
        string[] units = ["KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = -1;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return string.Create(CultureInfo.InvariantCulture, $"{v:0.0} {units[u]}");
    }
}

/// <summary>One node of the Indexed folders tree. Path is canonical (MailFolderPath).</summary>
public sealed record FolderTreeNode(string Name, string Path, bool Checked, bool Missing, IReadOnlyList<FolderTreeNode> Children);

public static class FolderTree
{
    private sealed class Node(string name, string path)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public bool Available;
        public bool Checked;
        public List<Node> Children { get; } = [];
    }

    /// <summary>
    /// Builds the tree from the store's canonical folder paths (null = Outlook didn't answer) and the
    /// mailbox's configured folders. Order: Inbox, Sent Items, then the rest by name; children by name.
    /// Never throws; blank/invalid configured entries are skipped.
    /// </summary>
    public static IReadOnlyList<FolderTreeNode> Build(IReadOnlyList<string>? available, IReadOnlyList<string> configured)
    {
        var roots = new List<Node>();
        var all = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);

        Node Ensure(string canonical, bool isAvailable)
        {
            var segs = canonical.Split('\\');
            var level = roots;
            Node? last = null;
            for (int i = 0; i < segs.Length; i++)
            {
                var path = string.Join('\\', segs.Take(i + 1));
                if (!all.TryGetValue(path, out var n))
                {
                    n = new Node(segs[i], path);
                    all[path] = n;
                    level.Add(n);
                }
                if (isAvailable) n.Available = true;
                level = n.Children;
                last = n;
            }
            return last!;
        }

        foreach (var p in available ?? [])
            if (TryCanonical(p) is { } c) Ensure(c, true);
        foreach (var p in configured ?? [])
            if (TryCanonical(p) is { } c) Ensure(c, false).Checked = true;

        return Convert(roots, available is not null, true);
    }

    private static string? TryCanonical(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try { return MailFolderPath.Parse(p).Canonical; }
        catch (ArgumentException) { return null; }
    }

    private static List<FolderTreeNode> Convert(List<Node> nodes, bool known, bool top)
    {
        IEnumerable<Node> ordered = nodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
        if (top)
        {
            var first = nodes.Where(n => n.Name == MailFolderPath.Inbox).Concat(nodes.Where(n => n.Name == MailFolderPath.SentItems));
            ordered = first.Concat(ordered.Where(n => n.Name != MailFolderPath.Inbox && n.Name != MailFolderPath.SentItems));
        }
        return ordered.Select(n => new FolderTreeNode(n.Name, n.Path, n.Checked, known && !n.Available,
            Convert(n.Children, known, false))).ToList();
    }
}
