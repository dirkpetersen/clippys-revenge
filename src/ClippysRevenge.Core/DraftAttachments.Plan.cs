namespace ClippysRevenge.Core;

/// <summary>Exactly one of Attachment and Link is set. Mapped is set only for links (plan-time mapping).
/// SkipReason: set when the item is not read at all (an AttachmentReasons text).</summary>
public sealed record PlannedAttachment(int Tier, AttachmentOrigin Origin, string EntryId,
    string? SenderName, string? SenderEmail, DateTimeOffset MessageTime, bool SharedAllowed,
    AttachmentInfo? Attachment, FoundLink? Link, MappedLink? Mapped, string? SkipReason)
{
    public string Name => Attachment is not null ? Attachment.FileName.Trim() : Link!.Name;
    public AttachmentKind Kind => Link is null ? AttachmentKind.File : AttachmentKind.Link;
}

/// <summary>SPEC 26.1 and 26.3: which attachments and links to read, in which order. Pure.</summary>
public static class DraftAttachmentPlan
{
    private const int MinMentionChars = 4;

    /// <summary>SPEC 26.1 sources, tiers, mention detection, dedupe, caps; 26.3 sender rule and mapping for
    /// links. Pure; never throws except ArgumentNullException (request, roots). Result is in tier order.</summary>
    public static IReadOnlyList<PlannedAttachment> Build(AttachmentRequest request, IReadOnlyList<SyncedRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(roots);

        var ownText = MailText.OwnText(request.AnchorBody ?? "", request.SignatureText);
        var candidates = new List<PlannedAttachment>();

        void AddMessage(AttachmentOrigin origin, string entryId, string? senderName, string? senderEmail,
            DateTimeOffset time, IReadOnlyList<AttachmentInfo>? attachments, string? body, bool anchor)
        {
            var shared = MayOpenShared(senderEmail, request.Identity, request.Mailbox);
            int TierOf(string name) => anchor ? 1 : IsMentioned(request.AnchorSubject, ownText, name) ? 2 : 3;

            foreach (var a in (attachments ?? []).Where(a => !a.IsInline).OrderBy(a => a.Position))
            {
                var name = (a.FileName ?? "").Trim();
                candidates.Add(new PlannedAttachment(TierOf(name), origin, entryId, senderName, senderEmail, time,
                    shared, a, null, null, null));
            }
            // Links only from the message's own text: a link in quoted history belongs to an earlier sender.
            var own = anchor ? ownText : MailText.OwnText(body ?? "", request.SignatureText);
            foreach (var link in OneDriveLinks.Find(WithoutQuotedLines(own)))
            {
                candidates.Add(new PlannedAttachment(TierOf(link.Name), origin, entryId, senderName, senderEmail, time,
                    shared, null, link, null, null));
            }
        }

        AddMessage(AttachmentOrigin.Email, request.AnchorEntryId, request.AnchorSenderName, request.AnchorSenderEmail,
            request.AnchorTime, request.AnchorAttachments, request.AnchorBody, anchor: true);
        foreach (var t in (request.Thread ?? [])
                     .Where(t => !string.Equals(t.EntryId, request.AnchorEntryId, StringComparison.Ordinal))
                     .OrderByDescending(t => t.EventTime))
        {
            AddMessage(AttachmentOrigin.Thread, t.EntryId, t.SenderName, t.SenderEmail, t.EventTime, t.Attachments,
                t.Body, anchor: false);
        }

        // OrderBy is stable: within a tier the order of the steps above stands.
        var sorted = candidates.OrderBy(c => c.Tier).Select(c => PreRead(c, roots)).ToList();

        // Dedupe: first occurrence (best tier, then newest) wins.
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<PlannedAttachment>();
        foreach (var p in sorted)
        {
            if (p.Attachment is { } att)
            {
                if (att.Size is > 0 && !seenFiles.Add(p.Name + "\u0000" + att.Size.Value)) continue;
            }
            else
            {
                var path = (p.Mapped as MappedLink.Local)?.Path;
                var dupUrl = seenUrls.Contains(p.Link!.NormalizedUrl);
                var dupPath = path is not null && seenPaths.Contains(path);
                if (dupUrl || dupPath) continue;
                seenUrls.Add(p.Link.NormalizedUrl);
                if (path is not null) seenPaths.Add(path);
            }
            kept.Add(p);
        }

        // Cap: only items that need a read use a slot.
        var slots = 0;
        for (var i = 0; i < kept.Count; i++)
        {
            if (kept[i].SkipReason is not null) continue;
            if (++slots > DraftAttachments.MaxItems)
                kept[i] = kept[i] with { SkipReason = AttachmentReasons.TooManyFiles };
        }
        return kept;
    }

    /// <summary>Own text without lines that start with ">": an inline reply keeps the quoted lines (MailText.OwnText cuts
    /// only a trailing quote block), and a link there belongs to the earlier sender.</summary>
    private static string WithoutQuotedLines(string text)
    {
        if (!text.Contains('>')) return text;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', lines.Where(l => !l.TrimStart(' ', '\t', '\u00A0').StartsWith('>')));
    }

    // InlineAttachments.FileNameFor's last-resort name "attachment-<position>" (no FileName, no DisplayName).
    private static bool IsFallbackName(string name) =>
        name.StartsWith("attachment-", StringComparison.Ordinal)
        && name.Length > 11 && name.AsSpan(11).IndexOfAnyExceptInRange('0', '9') < 0;

    private static PlannedAttachment PreRead(PlannedAttachment p, IReadOnlyList<SyncedRoot> roots)
    {
        if (p.Link is { } link)
        {
            if (!p.SharedAllowed) return p with { SkipReason = AttachmentReasons.FromOutside };
            var mapped = OneDriveMap.Map(link.Url, roots);
            return mapped is MappedLink.Unmappable
                ? p with { Mapped = mapped, SkipReason = AttachmentReasons.NotSynced }
                : p with { Mapped = mapped };
        }

        var name = p.Name;
        switch (AttachmentText.FormatOf(name))
        {
            case AttachmentFormat.Image:
                return p with { SkipReason = AttachmentReasons.Image };
            case AttachmentFormat.Unsupported:
            case AttachmentFormat.UnsupportedNoted:
                var ext = AttachmentText.Extension(name);
                // No extension: older index rows hold an embedded item's bare display name; the COM read
                // knows the real type, so let it through. The "attachment-<n>" fallback name has no name to
                // go on at all, so it is skipped here as well, as are other unreadable extensions.
                if (ext != "msg" && (ext.Length > 0 || IsFallbackName(name))) return p with { SkipReason = AttachmentReasons.CantRead(ext) };
                break;
        }
        return p;
    }

    /// <summary>SPEC 26.3 sender rule: sender is the own SMTP, the shared mailbox's address (mailbox != ""), or an
    /// address whose domain equals the own SMTP's domain or ends with "." + it. Case-insensitive. False for a
    /// sender that isn't an SMTP address (MailAddresses.LooksLikeSmtp) and when nothing matches.</summary>
    public static bool MayOpenShared(string? senderEmail, AiIdentity identity, string mailbox)
    {
        var sender = senderEmail?.Trim();
        if (!MailAddresses.LooksLikeSmtp(sender)) return false;

        var own = identity?.OwnSmtp;
        if (!string.IsNullOrWhiteSpace(own) && sender!.Equals(own.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(mailbox) && sender!.Equals(mailbox.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        var ownDomain = DomainOf(own);
        var d = DomainOf(sender);
        if (ownDomain is null || d is null) return false;
        // A consumer mail domain is not "your organization": only the user's own address counts there.
        if (IsConsumerDomain(ownDomain)) return false;
        return d == ownDomain || d.EndsWith("." + ownDomain, StringComparison.Ordinal);
    }

    /// <summary>Well-known consumer mail domains (SPEC 26.3): never treated as the user's organization.</summary>
    public static readonly IReadOnlyList<string> ConsumerMailDomains =
    [
        "outlook.com", "hotmail.com", "live.com", "msn.com", "gmail.com", "googlemail.com", "yahoo.com",
        "icloud.com", "me.com", "aol.com", "proton.me", "protonmail.com", "web.de",
        "hotmail.co.uk", "hotmail.fr", "hotmail.de", "outlook.de", "outlook.fr", "live.co.uk", "live.de",
        "live.fr", "yahoo.co.uk", "yahoo.de", "yahoo.fr", "ymail.com", "rocketmail.com", "mail.com",
        "t-online.de", "qq.com", "163.com", "126.com", "yandex.ru", "yandex.com", "mail.ru",
        "comcast.net", "verizon.net", "att.net", "fastmail.com", "zoho.com",
    ];

    private static bool IsConsumerDomain(string domain)
    {
        foreach (var c in ConsumerMailDomains)
            if (domain == c || domain.EndsWith("." + c, StringComparison.Ordinal)) return true;
        // gmx.com, gmx.net, gmx.de, ...
        if (domain.StartsWith("gmx.", StringComparison.Ordinal)) return true;
        // Regional variants of the big providers: <name>.<cc>, <name>.co.<cc>, <name>.com.<cc> (exactly that
        // second-level shape, so outlook.<cc> is consumer but a corporate name like outlook.contoso.com is not).
        var parts = domain.Split('.');
        if (parts.Length is < 2 or > 3 || parts[0] is not ("hotmail" or "outlook" or "live" or "yahoo")) return false;
        var cc = parts[^1];
        if (cc.Length != 2 || !char.IsAsciiLetter(cc[0]) || !char.IsAsciiLetter(cc[1])) return false;
        return parts.Length == 2 || parts[1] is "co" or "com";
    }

    private static string? DomainOf(string? address)
    {
        if (address is null) return null;
        var at = address.LastIndexOf('@');
        if (at < 0) return null;
        var d = address[(at + 1)..].Trim().ToLowerInvariant();
        if (d.EndsWith('.')) d = d[..^1];
        return d.Length == 0 ? null : d;
    }

    /// <summary>SPEC 26.1 tier-2 test against subject and own text (whole-token, case-insensitive).</summary>
    public static bool IsMentioned(string? subject, string ownText, string name)
    {
        var trimmed = (name ?? "").Trim();
        var candidates = new List<string> { trimmed };
        if (AttachmentText.Extension(trimmed).Length > 0)
        {
            var dot = trimmed.LastIndexOf('.');
            if (dot > 0) candidates.Add(trimmed[..dot].Trim());
        }

        foreach (var c in candidates)
        {
            if (c.Length < MinMentionChars) continue;
            if (ContainsToken(subject, c) || ContainsToken(ownText, c)) return true;
        }
        return false;
    }

    private static bool ContainsToken(string? haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack)) return false;
        var from = 0;
        while (from <= haystack.Length - needle.Length)
        {
            var i = haystack.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            var end = i + needle.Length;
            var before = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]);
            var after = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            if (before && after) return true;
            from = i + 1;
        }
        return false;
    }
}
