using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

public enum DontDraftKind { Sender, Domain }

/// <summary>Exact texts (SPEC 27.7).</summary>
public static class DontDraftTexts
{
    public const string NoAddress = "I couldn't find that sender's email address.";
    public const string OwnAddress = "That's your own address.";
    public const string OwnDomain =
        "That's your own organization's domain. To stop drafting for all internal mail, add it in Settings → AI drafts.";
    public const string Full =
        "\"Never draft for\" already has 200 entries. Remove some in Settings → AI drafts.";
    public const string AddButton = "Add";
    public const string CancelButton = "Cancel";

    /// <summary>"&lt;what&gt; is already on "Never draft for"."</summary>
    public static string Already(string what) => what + " is already on \"Never draft for\".";

    /// <summary>"Never draft replies to anyone at &lt;domain&gt;?"</summary>
    public static string ConfirmHeading(string domain) => "Never draft replies to anyone at " + domain + "?";

    /// <summary>The balloon after an entry was added.</summary>
    public static string Added(DontDraftKind kind, string entry) =>
        kind == DontDraftKind.Domain
            ? "I won't draft replies to anyone at " + entry + " any more. You can undo this in Settings → AI drafts."
            : "I won't draft replies to " + entry + " any more. You can undo this in Settings → AI drafts.";

    /// <summary>The Activity event text; carries no address.</summary>
    public static string AddedEvent(int count) =>
        "AI drafts: added an entry to \"Never draft for\" (" + count.ToString(CultureInfo.InvariantCulture)
        + (count == 1 ? " entry)." : " entries).");
}

/// <summary>Pure checks (SPEC 27.7). Never throw.</summary>
public static class DontDraftRules
{
    public static string? SenderAddress(string? senderEmail) =>
        MailAddresses.LooksLikeSmtp(senderEmail) ? senderEmail!.Trim() : null;

    public static string? Domain(string? address)
    {
        if (address is null) return null;
        var at = address.LastIndexOf('@');
        if (at < 0) return null;
        var d = address[(at + 1)..].Trim().ToLowerInvariant();
        return SenderPattern.TryParse("@" + d, out var p) && p.IsDomain ? p.Value : null;
    }

    public static bool IsOwnAddress(string address, string? ownSmtp, string? mailboxSmtp)
    {
        var a = address?.Trim();
        if (string.IsNullOrEmpty(a)) return false;
        var o = ownSmtp?.Trim();
        var m = mailboxSmtp?.Trim();
        return (!string.IsNullOrEmpty(o) && string.Equals(a, o, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(m) && string.Equals(a, m, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsOwnDomain(string domain, string? ownSmtp)
    {
        var d = Domain(ownSmtp);
        if (d is null || string.IsNullOrEmpty(domain)) return false;
        return domain == d
            || d.EndsWith("." + domain, StringComparison.Ordinal)
            || domain.EndsWith("." + d, StringComparison.Ordinal);
    }

    public static bool AddressCovered(IReadOnlyList<string?>? entries, string address)
    {
        if (entries is null) return false;
        foreach (var e in entries)
            if (SenderPattern.TryParse(e, out var p) && p.Matches(address)) return true;
        return false;
    }

    public static bool DomainCovered(IReadOnlyList<string?>? entries, string domain)
    {
        if (entries is null) return false;
        foreach (var e in entries)
            if (SenderPattern.TryParse(e, out var p) && p.IsDomain
                && (domain == p.Value || domain.EndsWith("." + p.Value, StringComparison.Ordinal)))
                return true;
        return false;
    }

    public static bool IsFull(IReadOnlyList<string?>? entries) =>
        entries is not null && entries.Count >= AiDraftingSettings.MaxSenderEntries;
}

/// <summary>The result of PrepareAsync. Exactly one of Entry and Balloon is non-null.</summary>
public sealed record DontDraftPrepared(DontDraftKind Kind, string? Entry, string? Balloon)
{
    /// <summary>Kind == Domain and Entry is not null.</summary>
    public bool NeedsConfirm => Kind == DontDraftKind.Domain && Entry is not null;
}

/// <summary>Both tray items (SPEC 27.7). Settings go through AiDraftingToggler.EditAi.</summary>
public sealed class DontDraftService
{
    private readonly IAiMailbox _mailbox;
    private readonly MailboxIndexes _indexes;
    private readonly AiDraftingToggler _toggler;
    private readonly Func<AiDraftingSettings> _live;
    private readonly ActivityHub _hub;
    private readonly ILogger? _logger;

    /// <param name="live">() => settings.AiDrafting (read-only use in PrepareAsync).</param>
    /// <exception cref="ArgumentNullException">mailbox, indexes, toggler, live or hub is null.</exception>
    public DontDraftService(IAiMailbox mailbox, MailboxIndexes indexes, AiDraftingToggler toggler,
        Func<AiDraftingSettings> live, ActivityHub hub, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(toggler);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(hub);
        _mailbox = mailbox;
        _indexes = indexes;
        _toggler = toggler;
        _live = live;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>Reads the selection and runs every check. Saves nothing. Never throws except OperationCanceledException for ct.</summary>
    public async Task<DontDraftPrepared> PrepareAsync(DontDraftKind kind, CancellationToken ct)
    {
        DontDraftPrepared Fail(string balloon) => new(kind, null, balloon);
        try
        {
            var sel = await _mailbox.GetSelectedAsync(ct).ConfigureAwait(false);
            if (sel is null) return Fail(DraftTexts.ManualNoSelection);
            var email = await _mailbox.ReadEmailAsync(sel.EntryId, ct).ConfigureAwait(false);
            if (email is null) return Fail(DraftTexts.ManualNotInMailbox);

            var address = DontDraftRules.SenderAddress(email.Email.SenderEmail);
            if (address is null) return Fail(DontDraftTexts.NoAddress);

            var owner = await AiOwner.ResolveAsync(_mailbox, _indexes, WorkPriority.Interactive, ct).ConfigureAwait(false);
            if (owner is null) return Fail(AiTabTexts.OwnerUnknown);

            var entries = _live().ExcludeSenders;
            string entry;
            if (kind == DontDraftKind.Sender)
            {
                if (DontDraftRules.IsOwnAddress(address, owner.Email, email.Mailbox)) return Fail(DontDraftTexts.OwnAddress);
                if (DontDraftRules.AddressCovered(entries, address)) return Fail(DontDraftTexts.Already(address));
                entry = address;
            }
            else
            {
                var domain = DontDraftRules.Domain(address);
                if (domain is null) return Fail(DontDraftTexts.NoAddress);
                if (DontDraftRules.IsOwnDomain(domain, owner.Email)) return Fail(DontDraftTexts.OwnDomain);
                if (DontDraftRules.DomainCovered(entries, domain)) return Fail(DontDraftTexts.Already(domain));
                entry = domain;
            }
            if (DontDraftRules.IsFull(entries)) return Fail(DontDraftTexts.Full);
            return new DontDraftPrepared(kind, entry, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OutlookUnavailableException)
        {
            return Fail(BalloonTexts.OutlookUnavailable);
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            return Fail(AiMenuTexts.ToggleFailed("something went wrong (" + ex.GetType().Name + ")"));
        }
    }

    /// <summary>Appends prepared.Entry through EditAi, re-checking "covered" and the limit on the live copy.
    /// Returns the balloon text. Records the Ai event only when the entry was added.</summary>
    /// <exception cref="ArgumentNullException">prepared is null.</exception>
    /// <exception cref="ArgumentException">prepared.Entry is null.</exception>
    public string Apply(DontDraftPrepared prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Entry is null) throw new ArgumentException("Entry is null.", nameof(prepared));
        var entry = prepared.Entry;
        var outcome = 0;   // 0 added, 1 already, 2 full
        var count = 0;
        try
        {
            var errors = _toggler.EditAi(ai =>
            {
                var list = ai.ExcludeSenders ??= [];
                var covered = prepared.Kind == DontDraftKind.Domain
                    ? DontDraftRules.DomainCovered(list, entry)
                    : DontDraftRules.AddressCovered(list, entry);
                if (covered) { outcome = 1; return false; }
                if (DontDraftRules.IsFull(list)) { outcome = 2; return false; }
                list.Add(entry);
                count = list.Count;
                return true;
            });
            if (errors is null) return outcome == 2 ? DontDraftTexts.Full : DontDraftTexts.Already(entry);
            if (errors.Count > 0) return errors[0].Message;
            _hub.Event(ActivityKind.Ai, DontDraftTexts.AddedEvent(count));
            _logger?.LogInformation("AI drafts: added a \"Never draft for\" entry ({Kind}), {Count} entries.",
                prepared.Kind == DontDraftKind.Domain ? "domain" : "address", count);
            return DontDraftTexts.Added(prepared.Kind, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AiMenuTexts.ToggleFailed(ex.Message);
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            return AiMenuTexts.ToggleFailed("something went wrong (" + ex.GetType().Name + ")");
        }
    }

    private void LogFailure(Exception ex) =>
        _logger?.LogWarning("AI drafts: \"Don't draft for\" failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
}
