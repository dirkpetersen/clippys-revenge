using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>The consent dialog (SPEC 19.10). Exact texts.</summary>
public static class AiConsent
{
    public const string Title = "Turn on AI drafts?";
    public const string Text = "Clippy will send new emails from your Inbox, earlier messages in the same thread, and samples of your sent mail to Claude (Anthropic) to write reply drafts in your style. Drafts are only saved, never sent. With Claude Code, this uses your Claude plan's usage. Shared mailboxes are included only if you turn them on one by one.";
    public const string TurnOnButton = "Turn on";
    public const string CancelButton = "Cancel";
}

public static class AiMenuTexts
{
    public const string Submenu = "AI drafts";
    public const string Auto = "Draft replies automatically";
    public const string DraftSelected = "Draft a reply to the selected email";
    public const string ShowDrafts = "Show Drafts in Outlook";
    public const string Settings = "AI drafts settings…";

    public static string ToggleFailed(string reason) => "Couldn't change AI drafts: " + reason;
}

public sealed record AiDraftsMenuModel(bool AutoChecked, bool AutoEnabled, bool DraftSelectedEnabled,
    bool ShowDraftsVisible, bool ShowDraftsEnabled);

public static class AiDraftsMenu
{
    /// <param name="live">Live AiDrafting settings.</param>
    /// <param name="available">The tray has AI services (the index opened and the worker exists).</param>
    /// <param name="outlookUnavailable">OutlookWorker.State == Unavailable.</param>
    /// <param name="busy">A tray toggle is in progress.</param>
    /// <exception cref="ArgumentNullException">live is null.</exception>
    public static AiDraftsMenuModel Build(AiDraftingSettings live, bool available, bool outlookUnavailable, bool busy)
    {
        ArgumentNullException.ThrowIfNull(live);
        return new AiDraftsMenuModel(
            AutoChecked: live.Enabled && live.HasConsent,
            AutoEnabled: available && !busy,
            DraftSelectedEnabled: available && !outlookUnavailable,
            ShowDraftsVisible: !outlookUnavailable,
            ShowDraftsEnabled: available);
    }
}

public enum AiToggleStep { TurnOff, AskConsent, CheckBackend }
public enum AiToggleOutcome { TurnOn, OpenSettings }

public static class AiToggle
{
    /// <exception cref="ArgumentNullException">live is null.</exception>
    public static AiToggleStep Next(AiDraftingSettings live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Enabled) return AiToggleStep.TurnOff;
        return live.HasConsent ? AiToggleStep.CheckBackend : AiToggleStep.AskConsent;
    }

    /// <exception cref="ArgumentNullException">status is null.</exception>
    public static AiToggleOutcome AfterCheck(ModelStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Ready ? AiToggleOutcome.TurnOn : AiToggleOutcome.OpenSettings;
    }
}

/// <summary>Which AI tab control shows an error for a settings field.</summary>
public enum AiTabField
{
    None, Enabled, Backend, ClaudeCliPath, Model, Effort, DailyCap, MinConfidence, MaxAgeDays,
    RequireDirectRecipient, ExcludeSenders, IncludeOnlySenders, AutoRegenerateStyle,
}

public static class AiTabFields
{
    private const string Prefix = AiDraftingSettings.FieldPrefix + ".";

    /// <summary>"AiDrafting.X" or "AiDrafting.X[i]" gives X. Anything else (incl. TimeoutSeconds, null) gives None.</summary>
    public static AiTabField For(string? field)
    {
        if (field is null || !field.StartsWith(Prefix, StringComparison.Ordinal)) return AiTabField.None;
        var name = field[Prefix.Length..];
        var bracket = name.IndexOf('[');
        if (bracket >= 0) name = name[..bracket];
        foreach (var v in Enum.GetValues<AiTabField>())
            if (v != AiTabField.None && string.Equals(v.ToString(), name, StringComparison.Ordinal)) return v;
        return AiTabField.None;
    }
}

public static class AiTabTexts
{
    public const string TabTitle = "AI drafts";
    public const string EnableText = "Draft replies to new email automatically";
    public const string UseLabel = "Use:";
    public const string UseClaudeCode = "Claude Code on this PC (your Claude subscription)";
    public const string UseApiKey = "Anthropic API key";
    public const string Checking = "Checking…";
    public const string CheckFailed = "✗ The check didn't finish. Press Check to try again.";
    public const string CheckNote = "Checking the saved settings. Press Apply first to check your changes.";
    public const string NoIndex = "AI drafts need the email index, which couldn't be opened. Check the tray icon for the reason.";
    public const string SignInHint = "Clippy never signs in to Claude for you. Run claude in a terminal and sign in there, or use an Anthropic API key.";
    public const string KeySaved = "A key is saved. It is never shown.";
    public const string NoKeySaved = "No key saved.";
    public const string KeyUnreadable = "The saved key can't be read. Save it again.";
    public const string RemoveKeyQuestion = "Remove the saved API key?";
    public const string OwnerUnknown = "Clippy doesn't know your email address yet. Start Outlook, wait for the first sync, then try again.";
    public const string NoSkillYet = "There is no writing style skill yet. Press Regenerate to create one.";
    public const string Regenerating = "Updating your writing style… this can take a few minutes.";
    public const string OpenedFolderInstead = "No app is set up to open .md files, so Clippy opened the folder instead.";
    public const string ViewTitle = "Writing style skill";
    public const string ManualLinkText = "What gets sent to Claude: see AI drafts in the manual";
    public const string ManualUrl = "https://dirkpetersen.github.io/clippys-revenge/ai-drafts/";
    public const string ListHint = "One address or domain per line.";

    private const int MaxEntryChars = 60;

    public static string KeySaveFailed(string reason) => "Couldn't save the key: " + reason;

    public static string OpenFailed(string reason) => "Couldn't open the file: " + reason;

    /// <summary>"Today: 3 of 30 Claude calls, 1 draft" / ", 0 drafts" / ", 2 drafts". N0, invariant.</summary>
    public static string Today(int calls, int cap, int drafts) =>
        "Today: " + calls.ToString("N0", CultureInfo.InvariantCulture) + " of " + cap.ToString("N0", CultureInfo.InvariantCulture)
        + " Claude calls, " + drafts.ToString("N0", CultureInfo.InvariantCulture) + (drafts == 1 ? " draft" : " drafts");

    /// <summary>CheckNote when Backend, Model or ClaudeCliPath (trimmed, OrdinalIgnoreCase) differ; else "".</summary>
    /// <exception cref="ArgumentNullException">draft or live is null.</exception>
    public static string PendingCheckNote(AiDraftingSettings draft, AiDraftingSettings live)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(live);
        var differs = draft.Backend != live.Backend
            || !string.Equals(draft.Model, live.Model, StringComparison.Ordinal)
            || !string.Equals((draft.ClaudeCliPath ?? "").Trim(), (live.ClaudeCliPath ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        return differs ? CheckNote : "";
    }

    /// <summary>The Regenerate result line (SPEC 19.10).</summary>
    /// <exception cref="ArgumentNullException">result is null.</exception>
    public static string StyleResult(StyleRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result switch
        {
            StyleRunResult.Updated u => "Writing style updated from " + u.SampleCount.ToString("N0", CultureInfo.InvariantCulture) + " sent emails.",
            StyleRunResult.TooFewEmails t => "Only " + t.Found.ToString("N0", CultureInfo.InvariantCulture) + " sent emails found. Clippy needs "
                + StyleCorpus.MinSamples.ToString(CultureInfo.InvariantCulture) + ".",
            StyleRunResult.Failed f => "Writing style not updated: " + f.Reason + ".",
            StyleRunResult.NoConsent => AiDraftingSettings.ConsentRequiredMessage,
            StyleRunResult.Busy => "Your writing style is already being updated.",
            _ => "Your writing style was kept.",
        };
    }

    private static readonly Regex SenderField = new(@"^AiDrafting\.(ExcludeSenders|IncludeOnlySenders)\[(\d+)\]\z",
        RegexOptions.CultureInvariant);

    /// <summary>The error message for the form. Never throws; draft may be null.</summary>
    public static string ErrorText(SettingsError error, AiDraftingSettings? draft)
    {
        try
        {
            var m = SenderField.Match(error.Field ?? "");
            if (m.Success && draft is not null && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var i))
            {
                var list = m.Groups[1].Value == "ExcludeSenders" ? draft.ExcludeSenders : draft.IncludeOnlySenders;
                if (list is not null && i >= 0 && i < list.Count && list[i] is { } entry)
                {
                    var sb = new StringBuilder();
                    foreach (var ch in entry.Trim())
                        if (!char.IsControl(ch)) sb.Append(ch);
                    return "\"" + TrayText.Truncate(sb.ToString(), MaxEntryChars) + "\": " + error.Message;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the plain message.
        }
        return error.Message;
    }
}

/// <summary>"One entry per line" text boxes (SPEC 19.10).</summary>
public static class AiSettingsLines
{
    /// <summary>Splits on \r\n, \n and \r, trims each line, drops blank lines. Null gives []. Order and duplicates are kept.</summary>
    public static List<string> Parse(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (var line in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var t = line.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }

    /// <summary>Entries joined with "\r\n". Null entries are skipped. Null list gives "".</summary>
    public static string Join(IEnumerable<string?>? entries) =>
        entries is null ? "" : string.Join("\r\n", entries.Where(e => e is not null));
}

public sealed record EditorTarget(string? Path, string? Message);

/// <summary>Open in editor (SPEC 19.10): only the user skill file, and only when it exists.</summary>
public static class StyleEditorTarget
{
    /// <exception cref="ArgumentNullException">paths or exists is null.</exception>
    public static EditorTarget Resolve(StylePaths paths, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(exists);
        var p = paths.UserSkillFile;
        if (!string.IsNullOrWhiteSpace(p)
            && System.IO.Path.IsPathFullyQualified(p)
            && string.Equals(System.IO.Path.GetFileName(p), "SKILL.md", StringComparison.OrdinalIgnoreCase)
            && exists(p))
            return new EditorTarget(p, null);
        return new EditorTarget(null, AiTabTexts.NoSkillYet);
    }
}

/// <summary>The own mailbox's owner for StyleSkill (SPEC 19.6).</summary>
public static class AiOwner
{
    /// <summary>Name = identity.DisplayName (trimmed). Email = identity.OwnSmtp, else the own index's meta "mailbox_smtp"
    /// (trimmed). Null when that isn't a valid address. A failing identity counts as ("",""). Never throws except
    /// OperationCanceledException for ct.</summary>
    /// <exception cref="ArgumentNullException">mailbox or indexes is null.</exception>
    public static async Task<Recipient?> ResolveAsync(IAiMailbox mailbox, MailboxIndexes indexes, WorkPriority priority,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(indexes);
        ct.ThrowIfCancellationRequested();
        var name = "";
        var email = "";
        try
        {
            var id = await mailbox.GetIdentityAsync(priority, ct).ConfigureAwait(false);
            name = id?.DisplayName?.Trim() ?? "";
            email = id?.OwnSmtp?.Trim() ?? "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Outlook unavailable or anything else: fall back to the index.
        }
        ct.ThrowIfCancellationRequested();
        if (email.Length == 0)
        {
            try
            {
                email = indexes.For(MailboxIndexes.OwnMailbox).GetMeta("mailbox_smtp")?.Trim() ?? "";
            }
            catch (Exception)
            {
                email = "";
            }
        }
        return EmailAddress.IsValid(email) ? new Recipient(name, email) : null;
    }
}

/// <summary>Consent and the Enabled switch, applied to the live settings at once (tray toggle, form consent).
/// Copy-on-write under Settings.Gate, like SharedMailboxToggler.</summary>
public sealed class AiDraftingToggler
{
    private readonly Settings _settings;
    private readonly Action<Settings> _save;
    private readonly Action _changed;
    private readonly TimeProvider _time;

    /// <param name="save">store.Save. Called with the new settings before live changes.</param>
    /// <param name="changed">Called after every successful change. May be a no-op.</param>
    /// <exception cref="ArgumentNullException">settings, save or changed is null.</exception>
    public AiDraftingToggler(Settings settings, Action<Settings> save, Action changed, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(changed);
        _settings = settings;
        _save = save;
        _changed = changed;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Records consent when there is none, saves, and returns the live ConsentedAtUtc. When consent already
    /// exists: returns it, with no save and no changed().</summary>
    /// <exception cref="IOException">The save failed. Live is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">Same.</exception>
    public DateTimeOffset RecordConsent()
    {
        var did = Mutate(ai =>
        {
            if (ai.HasConsent) return false;
            ai.RecordConsent(_time.GetUtcNow());
            return true;
        });
        if (did) _changed();
        return _settings.AiDrafting.ConsentedAtUtc!.Value;
    }

    /// <summary>SetEnabled on a clone, then save, then swap. No-op (no save, no changed()) when live.Enabled already equals enabled.</summary>
    /// <exception cref="InvalidOperationException">enabled is true without consent. Nothing is saved.</exception>
    /// <exception cref="IOException">The save failed. Live is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">Same.</exception>
    public void SetEnabled(bool enabled)
    {
        var did = Mutate(ai =>
        {
            if (ai.Enabled == enabled) return false;
            ai.SetEnabled(enabled, _time.GetUtcNow());
            return true;
        });
        if (did) _changed();
    }

    private bool Mutate(Func<AiDraftingSettings, bool> edit)
    {
        _settings.Gate.Wait();
        try
        {
            var next = _settings.Clone();
            if (!edit(next.AiDrafting)) return false;
            _save(next);
            _settings.AiDrafting = next.AiDrafting;
            return true;
        }
        finally
        {
            _settings.Gate.Release();
        }
    }
}

/// <summary>The status line's check (SPEC 19.3 "refreshed by Settings → Check", 19.10).</summary>
public sealed class AiStatusChecker
{
    private readonly IDraftModel _model;
    private readonly Action? _invalidate;
    private readonly Action<ModelStatus>? _report;
    private readonly ILogger? _logger;

    /// <param name="model">The DraftModelSwitch.</param>
    /// <param name="invalidate">ClaudeCliModel.InvalidateStatus, or null.</param>
    /// <param name="report">DraftWorker.ReportStatus, or null.</param>
    /// <exception cref="ArgumentNullException">model is null.</exception>
    public AiStatusChecker(IDraftModel model, Action? invalidate, Action<ModelStatus>? report, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _invalidate = invalidate;
        _report = report;
        _logger = logger;
    }

    /// <summary>refresh: invalidate() first. Then model.CheckAsync(ct), then report(status). Any exception other than
    /// OperationCanceledException for ct gives (false, CheckFailed), with a Warning log of the type name only.</summary>
    public async Task<ModelStatus> CheckAsync(bool refresh, CancellationToken ct)
    {
        if (refresh) Guarded(() => _invalidate?.Invoke());
        ModelStatus status;
        try
        {
            status = await _model.CheckAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            status = new ModelStatus(false, AiTabTexts.CheckFailed);
        }
        Guarded(() => _report?.Invoke(status));
        return status;
    }

    private void Guarded(Action a)
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            LogFailure(ex);
        }
    }

    private void LogFailure(Exception ex) =>
        _logger?.LogWarning("AI drafts: status check failed ({Type}).", ex.GetType().Name);
}
