using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>One validation failure. Field is a property path, e.g. "Port",
/// "AllowedFolders[1]", "SharedMailboxes[0].Smtp". Message is a short
/// sentence shown next to the field.</summary>
public sealed record SettingsError(string Field, string Message);

public sealed class SharedMailbox
{
    public string Smtp { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> IndexedFolders { get; set; } = [.. Settings.DefaultIndexedFolders];
    public int? RetentionMonths { get; set; }

    /// <summary>AI reply drafts for this mailbox's Inbox (SPEC 5.2, 19.7). Only used while AiDrafting.Enabled.</summary>
    public bool AiDrafts { get; set; }

    /// <summary>"Helpdesk &lt;helpdesk@x.edu&gt;", or just the Smtp when DisplayName is blank.</summary>
    [JsonIgnore]
    public string MenuLabel => string.IsNullOrWhiteSpace(DisplayName) ? Smtp : $"{DisplayName} <{Smtp}>";

    public int EffectiveRetentionMonths(int globalRetentionMonths) => RetentionMonths ?? globalRetentionMonths;
}

public sealed class Settings
{
    public const int DefaultPort = 8765;
    public const int MinPort = 1024, MaxPort = 65535;
    public const int DefaultRetentionMonths = 12;
    public const int MinRetentionMonths = 1, MaxRetentionMonths = 120;
    public const int DefaultSyncIntervalSeconds = 300;
    public const int MinSyncIntervalSeconds = 30, MaxSyncIntervalSeconds = 3600;
    public const int DefaultWorkStartHour = 8, DefaultWorkEndHour = 17;
    public const int DefaultInviteConfirmTimeoutSeconds = 120;
    public const int MinInviteConfirmTimeoutSeconds = 30, MaxInviteConfirmTimeoutSeconds = 900;
    public const int MaxTimeZoneLabelLength = 16;
    public const string AttachmentFolderName = "Clippy's Revenge";
    public static IReadOnlyList<string> DefaultIndexedFolders { get; } = ["Inbox", "Sent Items"];

    /// <summary>
    /// Serializes every writer of the live instance (tray toggler, settings applier). Not part of the
    /// settings data: never serialized, never copied.
    /// </summary>
    [JsonIgnore]
    public SemaphoreSlim Gate { get; } = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    /// <summary>Defaults, with paths under Environment.SpecialFolder.UserProfile.</summary>
    public Settings() : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { }

    private Settings(string profile)
    {
        AttachmentDir = Path.Combine(profile, "Downloads", AttachmentFolderName);
        AllowedFolders = [AttachmentDir, Path.Combine(profile, "Documents")];
    }

    /// <summary>Defaults, with paths under the given profile directory (testable).</summary>
    public static Settings CreateDefault(string userProfileDir) => new(userProfileDir);

    public int Port { get; set; } = DefaultPort;
    public int RetentionMonths { get; set; } = DefaultRetentionMonths;
    public int SyncIntervalSeconds { get; set; } = DefaultSyncIntervalSeconds;
    public List<string> IndexedFolders { get; set; } = [.. DefaultIndexedFolders];
    public List<SharedMailbox> SharedMailboxes { get; set; } = [];
    public int WorkStartHour { get; set; } = DefaultWorkStartHour;
    public int WorkEndHour { get; set; } = DefaultWorkEndHour;
    public bool BusinessDaysOnly { get; set; } = true;
    public string TimeZoneLabel { get; set; } = "";
    public bool TentativeIsBusy { get; set; } = true;
    public MeetingInviteMode MeetingInviteMode { get; set; } = MeetingInviteMode.Send;
    public int InviteConfirmTimeoutSeconds { get; set; } = DefaultInviteConfirmTimeoutSeconds;
    public string AttachmentDir { get; set; }
    public List<string> AllowedFolders { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public bool NotifyOnErrors { get; set; } = true;
    public LogLevel LogLevel { get; set; } = LogLevel.Information;
    public UpdateMode Updates { get; set; } = UpdateMode.Install;
    public AiDraftingSettings AiDrafting { get; set; } = new();

    /// <summary>Empty list = valid. Never throws, never mutates.</summary>
    public IReadOnlyList<SettingsError> Validate()
    {
        var e = new List<SettingsError>();
        void Add(string f, string m) => e.Add(new SettingsError(f, m));

        if (Port < MinPort || Port > MaxPort) Add("Port", "Must be between 1024 and 65535.");
        if (RetentionMonths < MinRetentionMonths || RetentionMonths > MaxRetentionMonths)
            Add("RetentionMonths", "Must be between 1 and 120.");
        if (SyncIntervalSeconds < MinSyncIntervalSeconds || SyncIntervalSeconds > MaxSyncIntervalSeconds)
            Add("SyncIntervalSeconds", "Must be between 30 and 3600.");
        ValidateFolders("IndexedFolders", IndexedFolders, e);

        bool startOk = WorkStartHour is >= 0 and <= 23;
        bool endOk = WorkEndHour is >= 1 and <= 24;
        if (!startOk) Add("WorkStartHour", "Must be between 0 and 23.");
        if (!endOk) Add("WorkEndHour", "Must be between 1 and 24.");
        else if (startOk && WorkEndHour <= WorkStartHour)
            Add("WorkEndHour", "Must be later than the work start hour.");

        if (TimeZoneLabel is null || TimeZoneLabel.Trim().Length > MaxTimeZoneLabelLength
            || TimeZoneLabel.Any(char.IsControl))
            Add("TimeZoneLabel", "Must be empty or at most 16 printable characters.");
        if (!Enum.IsDefined(MeetingInviteMode))
            Add("MeetingInviteMode", "Must be Send, DraftOnly or Disabled.");
        if (InviteConfirmTimeoutSeconds < MinInviteConfirmTimeoutSeconds
            || InviteConfirmTimeoutSeconds > MaxInviteConfirmTimeoutSeconds)
            Add("InviteConfirmTimeoutSeconds", "Must be between 30 and 900.");

        var pathError = PathError(AttachmentDir);
        if (pathError is not null) Add("AttachmentDir", pathError);
        if (AllowedFolders is null) Add("AllowedFolders", "Must not be empty.");
        else
            for (int i = 0; i < AllowedFolders.Count; i++)
            {
                var m = PathError(AllowedFolders[i]);
                if (m is not null) Add($"AllowedFolders[{i}]", m);
            }

        if (!Enum.IsDefined(LogLevel))
            Add("LogLevel", "Must be one of Trace, Debug, Information, Warning, Error, Critical, None.");

        if (!Enum.IsDefined(Updates)) Add("Updates", "Unknown update mode.");

        if (SharedMailboxes is null) Add("SharedMailboxes", "Must not be empty.");
        else
        {
            var seen = new List<string>();
            for (int i = 0; i < SharedMailboxes.Count; i++)
            {
                var sm = SharedMailboxes[i];
                var p = $"SharedMailboxes[{i}]";
                if (sm is null) { Add(p, "Must not be empty."); continue; }
                var smtp = sm.Smtp?.Trim();
                if (!IsValidAddress(smtp))
                    Add(p + ".Smtp", "Must be an email address like name@example.org.");
                else if (seen.Contains(smtp!, StringComparer.OrdinalIgnoreCase))
                    Add(p + ".Smtp", "This mailbox is listed twice.");
                if (smtp is not null) seen.Add(smtp);
                ValidateFolders(p + ".IndexedFolders", sm.IndexedFolders, e);
                if (sm.RetentionMonths is { } r && (r < MinRetentionMonths || r > MaxRetentionMonths))
                    Add(p + ".RetentionMonths", "Must be between 1 and 120.");
            }
        }

        if (AiDrafting is null) Add("AiDrafting", "Must not be empty.");
        else e.AddRange(AiDrafting.Validate());
        return e;
    }

    private static void ValidateFolders(string field, List<string>? folders, List<SettingsError> e)
    {
        if (folders is null || folders.Count == 0)
        {
            e.Add(new SettingsError(field, "Pick at least one folder."));
            return;
        }
        var seen = new List<string>();
        for (int i = 0; i < folders.Count; i++)
        {
            var f = folders[i];
            if (string.IsNullOrWhiteSpace(f))
            {
                e.Add(new SettingsError($"{field}[{i}]", "Must not be empty."));
                continue;
            }
            var t = f.Trim();
            if (seen.Contains(t, StringComparer.OrdinalIgnoreCase))
                e.Add(new SettingsError($"{field}[{i}]", "Listed twice."));
            else
                seen.Add(t);
        }
    }

    internal static string? PathError(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "Must not be empty.";
        if (v.StartsWith(@"\\", StringComparison.Ordinal) || v.StartsWith("//", StringComparison.Ordinal))
            return "Network and device paths are not allowed.";
        if (!Path.IsPathFullyQualified(v)) return @"Must be a full path, like C:\Users\you\Documents.";
        return null;
    }

    private static bool IsValidAddress(string? s) => EmailAddress.IsValid(s);

    /// <summary>AttachmentDir first, then AllowedFolders, de-duplicated.</summary>
    public IReadOnlyList<string> EffectiveAllowedFolders()
    {
        var result = new List<string>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Take(string? x)
        {
            if (string.IsNullOrWhiteSpace(x)) return;
            var t = x.Trim();
            if (keys.Add(Path.TrimEndingDirectorySeparator(t))) result.Add(t);
        }
        Take(AttachmentDir);
        if (AllowedFolders is not null)
            foreach (var f in AllowedFolders) Take(f);
        return result;
    }

    /// <summary>Case-insensitive, trimmed lookup by SMTP. Null when absent.</summary>
    public SharedMailbox? FindSharedMailbox(string smtp)
    {
        var key = smtp?.Trim();
        if (key is null || SharedMailboxes is null) return null;
        foreach (var sm in SharedMailboxes)
            if (sm is not null && string.Equals(sm.Smtp?.Trim(), key, StringComparison.OrdinalIgnoreCase))
                return sm;
        return null;
    }

    /// <summary>Tray/Settings "check": add with defaults, or re-enable an existing entry.</summary>
    /// <exception cref="ArgumentException">smtp is not a valid address.</exception>
    public SharedMailbox AddOrEnableSharedMailbox(string smtp, string? displayName)
    {
        if (!IsValidAddress(smtp))
            throw new ArgumentException($"'{smtp}' is not a valid email address.", nameof(smtp));
        var existing = FindSharedMailbox(smtp);
        if (existing is not null)
        {
            existing.Enabled = true;
            if (!string.IsNullOrWhiteSpace(displayName)) existing.DisplayName = displayName.Trim();
            return existing;
        }
        var sm = new SharedMailbox
        {
            Smtp = smtp.Trim().ToLowerInvariant(),
            DisplayName = displayName?.Trim() ?? "",
        };
        SharedMailboxes.Add(sm);
        return sm;
    }

    /// <summary>Tray/Settings "uncheck": Enabled = false. False when not found.</summary>
    public bool DisableSharedMailbox(string smtp)
    {
        var sm = FindSharedMailbox(smtp);
        if (sm is null) return false;
        sm.Enabled = false;
        return true;
    }

    /// <summary>Deep copy (new lists, new SharedMailbox instances).</summary>
    public Settings Clone() => FromJson(ToJson());

    /// <summary>
    /// Overwrites every settable property with a deep copy of source's value. Lists and SharedMailbox
    /// instances are new objects; the existing list objects of this instance are never mutated
    /// (other threads may be enumerating them). Does not validate.
    /// </summary>
    /// <exception cref="ArgumentNullException">source is null.</exception>
    public void CopyFrom(Settings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var c = source.Clone();
        Port = c.Port;
        RetentionMonths = c.RetentionMonths;
        SyncIntervalSeconds = c.SyncIntervalSeconds;
        IndexedFolders = c.IndexedFolders;
        SharedMailboxes = c.SharedMailboxes;
        WorkStartHour = c.WorkStartHour;
        WorkEndHour = c.WorkEndHour;
        BusinessDaysOnly = c.BusinessDaysOnly;
        TimeZoneLabel = c.TimeZoneLabel;
        TentativeIsBusy = c.TentativeIsBusy;
        MeetingInviteMode = c.MeetingInviteMode;
        InviteConfirmTimeoutSeconds = c.InviteConfirmTimeoutSeconds;
        AttachmentDir = c.AttachmentDir;
        AllowedFolders = c.AllowedFolders;
        StartWithWindows = c.StartWithWindows;
        NotifyOnErrors = c.NotifyOnErrors;
        LogLevel = c.LogLevel;
        Updates = c.Updates;
        AiDrafting = c.AiDrafting;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <exception cref="JsonException">Malformed JSON or an unknown enum name.</exception>
    public static Settings FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
        if (s is null) return new Settings();
        var d = new Settings();
        s.TimeZoneLabel ??= "";
        s.AttachmentDir ??= d.AttachmentDir;
        s.IndexedFolders ??= [.. DefaultIndexedFolders];
        s.AllowedFolders ??= d.AllowedFolders;
        s.SharedMailboxes ??= [];
        s.AiDrafting ??= new AiDraftingSettings();
        s.AiDrafting.FillDefaults();
        foreach (var sm in s.SharedMailboxes)
        {
            if (sm is null) continue;
            sm.Smtp ??= "";
            sm.DisplayName ??= "";
            sm.IndexedFolders ??= [.. DefaultIndexedFolders];
        }
        return s;
    }
}
