namespace ClippysRevenge.Core;

/// <summary>Rule ids stored in ai_decisions.rule (SPEC 19.7). All match ^[a-z_]+$.</summary>
public static class DraftRuleIds
{
    public const string Self = "self", NoReply = "no_reply", AutoSubject = "auto_subject", Excluded = "excluded",
        NotIncluded = "not_included", NotDirect = "not_direct", Replied = "replied", Gone = "gone",
        NotNote = "not_note", List = "list", Auto = "auto", DraftExists = "draft_exists";
}

/// <summary>What one COM work item learned about a candidate (SPEC 19.7, 19.9). Plain data, no COM objects.</summary>
/// <param name="TransportHeaders">PR_TRANSPORT_MESSAGE_HEADERS, at most 65,536 chars, null when absent.</param>
/// <param name="LastVerb">PR_LAST_VERB_EXECUTED, null when absent.</param>
public sealed record CandidateFacts(string? MessageClass, string? InternetMessageId, string? ConversationTopic,
    string? TransportHeaders, int? LastVerb, bool DraftExists);

/// <summary>The "does this mail need a reply at all" rules of SPEC 19.7. Pure: no I/O, no logging, never throws for odd data.</summary>
public static class DraftRules
{
    public const int MaxHeaderChars = 65_536;
    public const int VerbReply = 102, VerbReplyAll = 103;

    private static readonly string[] NoReplyLocalParts =
        ["noreply", "no-reply", "no_reply", "donotreply", "do-not-reply", "mailer-daemon", "postmaster", "bounce", "bounces"];

    private static readonly string[] AutoSubjectPrefixes =
        ["Automatic reply:", "Autoreply:", "Out of Office", "Undeliverable:", "Delivery Status Notification"];

    /// <summary>The index rules, in SPEC order: self, no_reply, auto_subject, excluded, not_included, not_direct,
    /// replied. The first match wins. repliedInIndex is called only when every earlier rule passed. Null = no match.</summary>
    /// <param name="mailboxSmtp">The mailbox's own primary SMTP address (not blank).</param>
    /// <exception cref="ArgumentNullException">candidate, settings or repliedInIndex is null.</exception>
    /// <exception cref="ArgumentException">mailboxSmtp is null or blank.</exception>
    public static string? EvaluateIndex(DraftCandidate candidate, string mailboxSmtp, AiDraftingSettings settings,
        Func<bool> repliedInIndex)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(repliedInIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(mailboxSmtp);

        var sender = candidate.SenderEmail?.Trim();

        if (sender is not null && string.Equals(sender, mailboxSmtp.Trim(), StringComparison.OrdinalIgnoreCase))
            return DraftRuleIds.Self;

        if (IsNoReplySender(sender)) return DraftRuleIds.NoReply;

        var subject = candidate.Subject?.TrimStart();
        if (subject is not null)
        {
            foreach (var p in AutoSubjectPrefixes)
                if (subject.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return DraftRuleIds.AutoSubject;
        }

        foreach (var entry in settings.ExcludeSenders ?? [])
            if (SenderPattern.TryParse(entry, out var ex) && ex.Matches(sender)) return DraftRuleIds.Excluded;

        var include = new List<SenderPattern>();
        foreach (var entry in settings.IncludeOnlySenders ?? [])
            if (SenderPattern.TryParse(entry, out var inc)) include.Add(inc);
        if (include.Count > 0 && !include.Any(p => p.Matches(sender))) return DraftRuleIds.NotIncluded;

        if (settings.RequireDirectRecipient)
        {
            var me = mailboxSmtp.Trim();
            var direct = Addresses(candidate.To).Any(a => string.Equals(a, me, StringComparison.OrdinalIgnoreCase))
                || Addresses(candidate.Cc).Any(a => string.Equals(a, me, StringComparison.OrdinalIgnoreCase));
            if (!direct) return DraftRuleIds.NotDirect;
        }

        return repliedInIndex() ? DraftRuleIds.Replied : null;
    }

    private static bool IsNoReplySender(string? sender)
    {
        if (sender is null) return false;
        var at = sender.LastIndexOf('@');
        if (at < 0) return false;
        var local = sender[..at];
        var plus = local.IndexOf('+');
        if (plus >= 0) local = local[..plus];
        foreach (var n in NoReplyLocalParts)
            if (string.Equals(local, n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The COM rules: not_note, list, auto, replied (last verb), draft_exists. Null facts give gone.</summary>
    public static string? EvaluateCom(CandidateFacts? facts)
    {
        if (facts is null) return DraftRuleIds.Gone;

        if (!string.Equals(facts.MessageClass, "IPM.Note", StringComparison.OrdinalIgnoreCase))
            return DraftRuleIds.NotNote;

        var headers = MailText.Headers(facts.TransportHeaders);
        if (headers.ContainsKey("List-Id") || headers.ContainsKey("List-Unsubscribe")) return DraftRuleIds.List;
        if (headers.TryGetValue("Precedence", out var prec))
        {
            var p = prec.Trim();
            if (p.Equals("bulk", StringComparison.OrdinalIgnoreCase) || p.Equals("list", StringComparison.OrdinalIgnoreCase)
                || p.Equals("junk", StringComparison.OrdinalIgnoreCase)) return DraftRuleIds.List;
        }

        if (headers.TryGetValue("Auto-Submitted", out var auto))
        {
            var semi = auto.IndexOf(';');
            var v = (semi >= 0 ? auto[..semi] : auto).Trim();
            if (!v.Equals("no", StringComparison.OrdinalIgnoreCase)) return DraftRuleIds.Auto;
        }

        if (facts.LastVerb is VerbReply or VerbReplyAll) return DraftRuleIds.Replied;
        if (facts.DraftExists) return DraftRuleIds.DraftExists;
        return null;
    }

    /// <summary>The re-check before creating a draft (SPEC 19.7 step 7): gone, replied (last verb), draft_exists.</summary>
    public static string? EvaluateBeforeCreate(CandidateFacts? facts)
    {
        if (facts is null) return DraftRuleIds.Gone;
        if (facts.LastVerb is VerbReply or VerbReplyAll) return DraftRuleIds.Replied;
        if (facts.DraftExists) return DraftRuleIds.DraftExists;
        return null;
    }

    /// <summary>Addresses in a stored To/CC list ("Name &lt;smtp&gt;; bare@x; Name only"). Tolerant, never throws.
    /// Each ';'-separated entry gives the text between its last '&lt;' and the following '&gt;', else the trimmed entry.
    /// Only values that pass MailAddresses.LooksLikeSmtp are returned, trimmed, in order.</summary>
    public static IReadOnlyList<string> Addresses(string? list)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(list)) return result;
        foreach (var raw in list.Split(';'))
        {
            var entry = raw.Trim();
            var lt = entry.LastIndexOf('<');
            if (lt >= 0)
            {
                var gt = entry.IndexOf('>', lt + 1);
                if (gt > lt) entry = entry.Substring(lt + 1, gt - lt - 1).Trim();
            }
            if (MailAddresses.LooksLikeSmtp(entry)) result.Add(entry.Trim());
        }
        return result;
    }
}
