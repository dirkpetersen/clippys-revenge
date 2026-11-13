using System.Runtime.InteropServices;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

public static class ComSafe
{
    /// <summary>Runs f. A COMException that is not "gone" yields default; everything else propagates.</summary>
    public static T? Try<T>(Func<T> f)
    {
        ArgumentNullException.ThrowIfNull(f);
        try
        {
            return f();
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            return default;
        }
    }
}

/// <summary>Type: the OlAttachmentType value as int (olOLE = 6).</summary>
public sealed record AttachmentFacts(int Type, bool Hidden, string? ContentId, string? MimeTag, string FileName);

public static class InlineAttachments
{
    public const int OlOle = 6;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "jpe", "gif", "bmp", "dib", "tif", "tiff", "webp", "svg", "ico", "emf", "wmf", "heic",
    };

    public static bool IsImage(string? mimeTag, string? fileName)
    {
        if (mimeTag is not null && mimeTag.Trim().StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var name = fileName.Trim();
        var dot = name.LastIndexOf('.');
        return dot >= 0 && ImageExtensions.Contains(name[(dot + 1)..]);
    }

    private static bool IsCidChar(char c) => char.IsLetterOrDigit(c) || c is '.' or '@' or '_' or '-' or '%' or '+';

    public static bool IsReferenced(string? html, string? contentId)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(contentId)) return false;
        var cid = contentId.Trim();
        if (cid.Length >= 2 && cid[0] == '<' && cid[^1] == '>') cid = cid[1..^1].Trim();
        if (cid.Length == 0) return false;

        var needle = "cid:" + cid;
        var from = 0;
        while (from <= html.Length - needle.Length)
        {
            var i = html.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            var end = i + needle.Length;
            if (end >= html.Length || !IsCidChar(html[end])) return true;
            from = i + 1;
        }
        return false;
    }

    /// <summary>SPEC §8 rule. htmlBody is called at most once, and only when needed.</summary>
    public static bool IsInline(AttachmentFacts a, Func<string?> htmlBody)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(htmlBody);
        if (a.Type == OlOle) return true;
        if (a.Hidden) return true;
        if (IsImage(a.MimeTag, a.FileName) && !string.IsNullOrWhiteSpace(a.ContentId))
            return IsReferenced(htmlBody(), a.ContentId);
        return false;
    }

    public static string FileNameFor(string? fileName, string? displayName, int position)
    {
        if (!string.IsNullOrWhiteSpace(fileName)) return fileName.Trim();
        if (!string.IsNullOrWhiteSpace(displayName)) return displayName.Trim();
        return $"attachment-{position}";
    }
}

public static class MailAddresses
{
    public const int MaxRecipientsPerField = 500;

    public static string? ChooseSenderSmtp(string? emailType, string? senderEmailAddress,
        Func<string?> exchangeSmtp, Func<string?> senderSmtpProp)
    {
        if (string.Equals(emailType, "EX", StringComparison.OrdinalIgnoreCase))
            return First(exchangeSmtp, senderSmtpProp);
        return First(() => senderEmailAddress, senderSmtpProp);
    }

    public static string? ChooseRecipientSmtp(Func<string?> smtpProp, string? address, Func<string?> exchangeSmtp) =>
        First(smtpProp, () => address, exchangeSmtp);

    private static string? First(params Func<string?>[] candidates)
    {
        foreach (var c in candidates)
        {
            var v = c();
            if (LooksLikeSmtp(v)) return v!.Trim();
        }
        return null;
    }

    public static bool LooksLikeSmtp(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        var at = t.IndexOf('@');
        if (at <= 0 || at != t.LastIndexOf('@') || at == t.Length - 1) return false;
        return !t.Any(char.IsWhiteSpace);
    }

    public static string? FormatList(IEnumerable<(string? Name, string? Smtp)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var parts = new List<string>();
        foreach (var (rawName, rawSmtp) in entries.Take(MaxRecipientsPerField))
        {
            var name = rawName?.Trim() ?? "";
            var smtp = rawSmtp?.Trim() ?? "";
            if (LooksLikeSmtp(smtp))
            {
                if (name.Equals(smtp, StringComparison.OrdinalIgnoreCase)) name = "";
                parts.Add(Recipients.Format([new Recipient(name, smtp)]));
            }
            else if (name.Length > 0)
            {
                parts.Add(name);
            }
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}

public sealed class ItemNotFoundException : McpException
{
    public ItemNotFoundException(string? id) : base(BuildMessage(id?.Trim() ?? ""))
    {
        Id = id?.Trim() ?? "";
    }

    public string Id { get; }

    private static string BuildMessage(string id)
    {
        var shown = id.Length > 200 ? id[..200] + "…" : id;
        return $"Email \"{shown}\" was not found in Outlook. It may have been deleted or moved to a folder that isn't indexed. Run search_emails again to get a current id.";
    }
}

/// <summary>Worker-thread lookups the resolver needs. App implements it over COM; tests fake it.</summary>
public interface IMailItemSource<TItem> where TItem : class
{
    /// <summary>Store id of that mailbox's store, or null when it isn't mounted/configured.</summary>
    string? StoreIdFor(string mailbox);
    /// <summary>The mail item, or null when not found / not a MailItem. Gone COM errors propagate.</summary>
    TItem? TryGetById(string entryId, string storeId);
    /// <summary>Entry id of the item with that internet message id in that folder, or null.</summary>
    string? FindByMessageId(string mailbox, string folderPath, string internetMessageId);
}

public sealed record ResolvedMailItem<TItem>(TItem Item, string Mailbox, string EntryId, string StoreId)
    where TItem : class;

public static class MailItemResolver
{
    /// <summary>SPEC §8 GetItem chain. Throws ItemNotFoundException when every step fails.</summary>
    public static ResolvedMailItem<TItem> Resolve<TItem>(
        string? id,
        Func<string, LocatedEmail?> locate,
        IReadOnlyList<string> mailboxes,
        Func<string, IReadOnlyList<string>> indexedFolders,
        IMailItemSource<TItem> source) where TItem : class
    {
        ArgumentNullException.ThrowIfNull(locate);
        ArgumentNullException.ThrowIfNull(mailboxes);
        ArgumentNullException.ThrowIfNull(indexedFolders);
        ArgumentNullException.ThrowIfNull(source);

        var key = id?.Trim();
        if (string.IsNullOrEmpty(key)) throw new ItemNotFoundException(id);

        var located = locate(key);
        if (located is not null)
        {
            var storeId = !string.IsNullOrWhiteSpace(located.Email.StoreId)
                ? located.Email.StoreId
                : source.StoreIdFor(located.Mailbox);
            if (storeId is not null)
            {
                var item = source.TryGetById(key, storeId);
                if (item is not null) return new ResolvedMailItem<TItem>(item, located.Mailbox, key, storeId);

                var imid = located.Email.InternetMessageId;
                if (!string.IsNullOrWhiteSpace(imid))
                {
                    foreach (var folder in indexedFolders(located.Mailbox))
                    {
                        var newId = source.FindByMessageId(located.Mailbox, folder, imid);
                        if (newId is null) continue;
                        item = source.TryGetById(newId, storeId);
                        if (item is not null) return new ResolvedMailItem<TItem>(item, located.Mailbox, newId, storeId);
                    }
                }
            }
            throw new ItemNotFoundException(key);
        }

        foreach (var m in mailboxes)
        {
            var sid = source.StoreIdFor(m);
            if (sid is null) continue;
            var item = source.TryGetById(key, sid);
            if (item is not null) return new ResolvedMailItem<TItem>(item, m, key, sid);
        }
        throw new ItemNotFoundException(key);
    }
}

/// <summary>Missing: requested ids that couldn't be opened or read.</summary>
public sealed record MailReadBatch(IReadOnlyList<IndexedEmail> Emails, IReadOnlyList<string> Missing);

public sealed record LiveEmail(string Mailbox, IndexedEmail Email);
