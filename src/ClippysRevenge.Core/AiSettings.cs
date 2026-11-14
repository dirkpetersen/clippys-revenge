using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace ClippysRevenge.Core;

public enum AiBackend { ClaudeCode, ApiKey }

/// <summary>SPEC 19.2. Mutable settings data; deep-copied with Settings.Clone/CopyFrom.</summary>
public sealed partial class AiDraftingSettings
{
    public const string DefaultModel = "claude-opus-5-5";
    public const string DefaultEffort = "medium";
    public static IReadOnlyList<string> Efforts { get; } = ["low", "medium", "high", "xhigh", "max"];
    public const int MaxModelLength = 64;
    public const int DefaultDailyCap = 30, MinDailyCap = 1, MaxDailyCap = 500;
    public const double DefaultMinConfidence = 0.6;
    public const int DefaultMaxAgeDays = 2, MinMaxAgeDays = 1, MaxMaxAgeDays = 14;
    public const int DefaultTimeoutSeconds = 180, MinTimeoutSeconds = 30, MaxTimeoutSeconds = 900;
    public const int DefaultMeetingTimeOptions = 5, MinMeetingTimeOptions = 1, MaxMeetingTimeOptions = 10;
    public const int MaxSenderEntries = 200;
    public const string FieldPrefix = "AiDrafting";

    public const string ConsentRequiredMessage =
        "Turn on AI drafts from the tray or Settings so Clippy can ask for your consent first.";

    public bool Enabled { get; set; }
    public DateTimeOffset? ConsentedAtUtc { get; set; }
    public DateTimeOffset? EnabledAtUtc { get; set; }
    public AiBackend Backend { get; set; } = AiBackend.ClaudeCode;
    public string Model { get; set; } = DefaultModel;
    public string Effort { get; set; } = DefaultEffort;
    public int DailyCap { get; set; } = DefaultDailyCap;
    public double MinConfidence { get; set; } = DefaultMinConfidence;
    public int MaxAgeDays { get; set; } = DefaultMaxAgeDays;
    public bool RequireDirectRecipient { get; set; } = true;
    public List<string> ExcludeSenders { get; set; } = [];
    public List<string> IncludeOnlySenders { get; set; } = [];
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
    public string ClaudeCliPath { get; set; } = "";
    public bool AutoRegenerateStyle { get; set; } = true;

    /// <summary>Offer free times in drafts that answer a meeting request (SPEC 19.2, 21.6).</summary>
    public bool MeetingTimes { get; set; } = true;

    /// <summary>1-10 options per draft, both lists together (SPEC 19.2, 21.4).</summary>
    public int MeetingTimeOptions { get; set; } = DefaultMeetingTimeOptions;

    [JsonIgnore] public bool HasConsent => ConsentedAtUtc is not null;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._\[\]-]*\z")]
    private static partial Regex ModelRegex();

    /// <summary>The one model-name rule (settings validation and CLI arguments).</summary>
    internal static bool IsValidModelName(string? model) =>
        model is not null && model.Length > 0 && model.Length <= MaxModelLength && ModelRegex().IsMatch(model);

    /// <summary>Sets ConsentedAtUtc to nowUtc (converted to UTC) when it is null. Never changes or clears an existing value.</summary>
    public void RecordConsent(DateTimeOffset nowUtc) => ConsentedAtUtc ??= nowUtc.ToUniversalTime();

    /// <summary>
    /// The one way UI code turns automatic drafting on or off. Turning it on (false to true) sets
    /// EnabledAtUtc = nowUtc (UTC). true to true and turning it off leave EnabledAtUtc unchanged.
    /// </summary>
    /// <exception cref="InvalidOperationException">enabled is true and HasConsent is false. Nothing is changed.</exception>
    public void SetEnabled(bool enabled, DateTimeOffset nowUtc)
    {
        if (enabled)
        {
            if (!HasConsent) throw new InvalidOperationException(ConsentRequiredMessage);
            if (!Enabled) EnabledAtUtc = nowUtc.ToUniversalTime();
        }
        Enabled = enabled;
    }

    /// <summary>
    /// The oldest receive time a candidate may have (SPEC 19.7): max(nowUtc - MaxAgeDays, EnabledAtUtc ?? nowUtc).
    /// A null EnabledAtUtc gives nowUtc, so no existing mail qualifies. Result is UTC.
    /// </summary>
    public DateTimeOffset CandidateCutoff(DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        var byAge = now - TimeSpan.FromDays(MaxAgeDays);
        var enabledAt = EnabledAtUtc?.ToUniversalTime() ?? now;
        return byAge > enabledAt ? byAge : enabledAt;
    }

    /// <summary>Errors with fields "AiDrafting.&lt;Name&gt;", in the order of SPEC 19.2's table. Never throws, never mutates.</summary>
    public IReadOnlyList<SettingsError> Validate()
    {
        var e = new List<SettingsError>();
        void Add(string f, string m) => e.Add(new SettingsError($"{FieldPrefix}.{f}", m));

        if (Enabled && ConsentedAtUtc is null) Add("Enabled", ConsentRequiredMessage);
        if (!Enum.IsDefined(Backend)) Add("Backend", "Must be ClaudeCode or ApiKey.");
        if (!IsValidModelName(Model))
            Add("Model", "Must be a model name like claude-opus-5-5: up to 64 letters, digits, '.', '_', '-', '[' or ']', starting with a letter.");
        if (Effort is null || !Efforts.Contains(Effort, StringComparer.Ordinal))
            Add("Effort", "Must be low, medium, high, xhigh or max.");
        if (DailyCap < MinDailyCap || DailyCap > MaxDailyCap) Add("DailyCap", "Must be between 1 and 500.");
        if (double.IsNaN(MinConfidence) || double.IsInfinity(MinConfidence) || MinConfidence < 0.0 || MinConfidence > 1.0)
            Add("MinConfidence", "Must be between 0 and 1.");
        if (MaxAgeDays < MinMaxAgeDays || MaxAgeDays > MaxMaxAgeDays) Add("MaxAgeDays", "Must be between 1 and 14.");
        ValidateSenders("ExcludeSenders", ExcludeSenders, e);
        ValidateSenders("IncludeOnlySenders", IncludeOnlySenders, e);
        if (TimeoutSeconds < MinTimeoutSeconds || TimeoutSeconds > MaxTimeoutSeconds)
            Add("TimeoutSeconds", "Must be between 30 and 900.");

        if (!string.IsNullOrWhiteSpace(ClaudeCliPath))
        {
            var path = ClaudeCliPath.Trim();
            var pathError = Settings.PathError(path);
            if (pathError is not null) Add("ClaudeCliPath", pathError);
            // ClaudeCliLocator rejects '/' in Windows paths; on other OSes (Core tests) '/' is the separator.
            else if (OperatingSystem.IsWindows() && path.Contains('/')) Add("ClaudeCliPath", "Use backslashes in the Claude Code path.");
            else
            {
                var ext = Path.GetExtension(path);
                if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".js", StringComparison.OrdinalIgnoreCase))
                    Add("ClaudeCliPath", "Must be the path of claude.exe or of Claude Code's cli.js.");
            }
        }

        if (MeetingTimeOptions < MinMeetingTimeOptions || MeetingTimeOptions > MaxMeetingTimeOptions)
            Add("MeetingTimeOptions", "Must be between 1 and 10.");
        return e;
    }

    private static void ValidateSenders(string name, List<string>? list, List<SettingsError> e)
    {
        var field = $"{FieldPrefix}.{name}";
        if (list is null)
        {
            e.Add(new SettingsError(field, "Must not be empty."));
            return;
        }
        if (list.Count > MaxSenderEntries) e.Add(new SettingsError(field, "At most 200 entries."));
        for (int i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (string.IsNullOrWhiteSpace(entry))
                e.Add(new SettingsError($"{field}[{i}]", "Must not be empty."));
            else if (!SenderPattern.TryParse(entry, out _))
                e.Add(new SettingsError($"{field}[{i}]", "Must be an email address or a domain like example.org."));
        }
    }

    /// <summary>After deserialization: null Model/Effort become defaults, null lists become [], null ClaudeCliPath becomes "". Null list entries stay (Validate reports them).</summary>
    internal void FillDefaults()
    {
        Model ??= DefaultModel;
        Effort ??= DefaultEffort;
        ExcludeSenders ??= [];
        IncludeOnlySenders ??= [];
        ClaudeCliPath ??= "";
    }
}

/// <summary>One ExcludeSenders / IncludeOnlySenders entry: an address, or a domain that also matches its subdomains (SPEC 19.2, 19.7).</summary>
public readonly record struct SenderPattern(string Value, bool IsDomain)
{
    public const int MaxDomainLength = 253;

    /// <summary>
    /// Trims the entry. "@domain" or a string without '@' is a domain. A string containing '@' (not at index 0)
    /// is an address, valid per EmailAddress.IsValid. Value is lower-case (invariant). False for null/blank/invalid.
    /// </summary>
    public static bool TryParse(string? entry, out SenderPattern pattern)
    {
        pattern = default;
        var t = entry?.Trim();
        if (string.IsNullOrEmpty(t)) return false;
        var at = t.IndexOf('@');
        if (at == 0)
        {
            var d = t[1..];
            if (!IsValidDomain(d)) return false;
            pattern = new SenderPattern(d.ToLowerInvariant(), true);
            return true;
        }
        if (at > 0)
        {
            if (!EmailAddress.IsValid(t)) return false;
            pattern = new SenderPattern(t.ToLowerInvariant(), false);
            return true;
        }
        if (!IsValidDomain(t)) return false;
        pattern = new SenderPattern(t.ToLowerInvariant(), true);
        return true;
    }

    /// <summary>
    /// address is trimmed and lower-cased. An address pattern matches the same address exactly. A domain pattern
    /// matches when the part after the last '@' equals Value or ends with "." + Value. False for null, blank or a
    /// string without '@'.
    /// </summary>
    public bool Matches(string? address)
    {
        var a = address?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(a) || Value is null) return false;
        var at = a.LastIndexOf('@');
        if (at < 0) return false;
        if (!IsDomain) return string.Equals(a, Value, StringComparison.Ordinal);
        var domain = a[(at + 1)..];
        return domain == Value || domain.EndsWith("." + Value, StringComparison.Ordinal);
    }

    private static bool IsValidDomain(string d)
    {
        if (d.Length is < 1 or > MaxDomainLength) return false;
        var labels = d.Split('.');
        if (labels.Length < 2) return false;
        foreach (var l in labels)
        {
            if (l.Length is < 1 or > 63) return false;
            if (l[0] == '-' || l[^1] == '-') return false;
            foreach (var c in l)
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')) return false;
        }
        return true;
    }
}
