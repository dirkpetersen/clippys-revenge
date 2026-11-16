using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using ModelContextProtocol;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>Attachment export (SPEC §11.3, §11.4). Worker thread only. Content goes only to the chosen file on disk.</summary>
internal sealed partial class OutlookMail
{
    internal SavedAttachment SaveAttachment(Outlook.Application app, string id, string? filename, int? position, string dir)
    {
        var item = GetItem(app, id);
        try
        {
            var live = ReadAttachments(item);
            var target = AttachmentSelection.SelectOne(live, filename, position);
            return SaveOne(item, target, dir);
        }
        finally
        {
            Release(item);
        }
    }

    internal IReadOnlyList<AttachmentSaveOutcome> SaveAttachments(
        Outlook.Application app, string id, IReadOnlyList<AttachmentInfo> wanted, string dir)
    {
        var item = GetItem(app, id);
        try
        {
            var live = ReadAttachments(item);
            var outcomes = new List<AttachmentSaveOutcome>(wanted.Count);
            foreach (var w in wanted)
            {
                var m = AttachmentSelection.MatchLive(live, w);
                if (m is null)
                {
                    outcomes.Add(new AttachmentSaveOutcome(w, null,
                        $"Attachment \"{w.FileName}\" (position {w.Position}) is no longer on this email. It may have changed since it was indexed; run search_emails again."));
                    continue;
                }
                try
                {
                    outcomes.Add(new AttachmentSaveOutcome(w, SaveOne(item, m, dir), null));
                }
                catch (McpException ex) when (ex is not OutlookUnavailableException)
                {
                    outcomes.Add(new AttachmentSaveOutcome(w, null, ex.Message));
                }
            }
            return outcomes;
        }
        finally
        {
            Release(item);
        }
    }

    /// <summary>Worker thread only. See IAttachmentLinkSaver. abandoned: set by the caller after it gave up
    /// (timeout/cancel); a save that finishes afterwards deletes its file.</summary>
    internal SavedAttachment SaveAttachmentForLink(Outlook.Application app, string id, string? filename, int? position,
        string tempPath, long maxBytes, CancellationToken abandoned)
    {
        if (!LinkPaths.IsTempFileName(Path.GetFileName(tempPath)) || !Path.IsPathFullyQualified(tempPath))
            throw new ArgumentException("tempPath must be a link temp file.", nameof(tempPath));
        abandoned.ThrowIfCancellationRequested();

        var item = GetItem(app, id);
        try
        {
            var live = ReadAttachments(item);
            var target = AttachmentSelection.SelectOne(live, filename, position);
            if (target.Size is { } est && est > maxBytes)
                throw new AttachmentLinkException(LinkRefusal.TooBig, LinkTexts.TooBig(est));

            var atts = item.Attachments;
            Outlook.Attachment? att = null;
            try
            {
                att = atts[target.Position];
                att.SaveAsFile(tempPath);
                var length = new FileInfo(tempPath).Length;
                if (length > maxBytes)
                {
                    DeleteQuietly(tempPath);
                    throw new AttachmentLinkException(LinkRefusal.TooBig, LinkTexts.TooBig(length));
                }
                if (abandoned.IsCancellationRequested)
                {
                    DeleteQuietly(tempPath);
                    throw new OperationCanceledException(abandoned);
                }
                return new SavedAttachment(tempPath, target.FileName, length);
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                       && !ComErrors.IsOutlookGone(ex))
            {
                DeleteQuietly(tempPath);
                // COM and IO messages can contain the temp path: never put them in a result.
                var reason = ex is COMException ? $"Outlook error 0x{ex.HResult:X8}" : ex.GetType().Name;
                throw new McpException(
                    $"Could not save \"{target.FileName}\" for the download link ({reason}). Try again, or use get_attachment.");
            }
            finally
            {
                Release(att);
                Release(atts);
            }
        }
        finally
        {
            Release(item);
        }
    }

    /// <summary>SPEC 26.9. Worker thread only. Reads one attachment for an AI prompt; nothing is kept.
    /// Null when the email can't be opened. COM "Outlook gone" errors propagate (the worker turns them into
    /// OutlookUnavailableException).</summary>
    /// <exception cref="ArgumentNullException">app, mailbox, entryId, wanted or files null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">maxBytes &lt;= 0.</exception>
    internal AiAttachmentRead? ReadAttachmentForAi(Outlook.Application app, string mailbox, string entryId,
        AttachmentInfo wanted, long maxBytes, AiFiles files)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(entryId);
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        ResolvedMailItem<Outlook.MailItem> r;
        try
        {
            r = Resolve(app, entryId);
        }
        catch (ItemNotFoundException)
        {
            return null;
        }

        var item = r.Item;
        Outlook.Attachments? atts = null;
        Outlook.Attachment? att = null;
        Outlook.PropertyAccessor? pa = null;
        try
        {
            var live = ReadAttachments(item);
            var m = AttachmentSelection.MatchLive(live, wanted);
            if (m is null) return new AiAttachmentRead.NotOnEmail();
            atts = item.Attachments;
            att = atts[m.Position];
            var type = (int)att.Type;
            pa = ComSafe.Try(() => att.PropertyAccessor);
            var method = pa is null ? null : ComSafe.Try(() => pa.GetProperty(AiAttachmentCom.PrAttachMethod));
            if (AiAttachmentCom.IsCloud(method, type))
            {
                var longName = pa is null ? null : ComSafe.Try(() => pa.GetProperty(AiAttachmentCom.PrAttachLongFileName));
                var longPath = pa is null ? null : ComSafe.Try(() => pa.GetProperty(AiAttachmentCom.PrAttachLongPathName));
                return new AiAttachmentRead.Cloud(AiAttachmentCom.CloudUrl(longName, longPath),
                    ComSafe.Try(() => att.DisplayName) ?? m.FileName);   // nothing saved
            }
            var size = ComSafe.Try<long?>(() => (long)att.Size);
            if (size is { } s && s > maxBytes) return new AiAttachmentRead.TooLarge(s);   // nothing saved
            return type switch
            {
                AiAttachmentCom.OlByValue => ReadFileForAi(att, maxBytes, files),
                AiAttachmentCom.OlEmbeddedItem => ReadItemForAi(app, att, maxBytes, files),
                _ => new AiAttachmentRead.Failed("UnsupportedAttachmentType"),
            };
        }
        catch (Exception ex) when ((ex is COMException or IOException or UnauthorizedAccessException)
                                   && !ComErrors.IsOutlookGone(ex))
        {
            // AiFolderUnusableException is an IOException. Only the type name leaves this method.
            return new AiAttachmentRead.Failed(ex.GetType().Name);
        }
        finally
        {
            Release(pa);
            Release(att);
            Release(atts);
            Release(item);
        }
    }

    /// <summary>The one SaveAsFile of the AI read path (SPEC 26.11: three in this file). Returns the saved length.</summary>
    private static long SaveForAi(Outlook.Attachment att, string path)
    {
        att.SaveAsFile(path);
        return new FileInfo(path).Length;
    }

    private static AiAttachmentRead ReadFileForAi(Outlook.Attachment att, long maxBytes, AiFiles files)
    {
        var path = files.PrepareTempPath(item: false);
        try
        {
            var len = SaveForAi(att, path);
            if (len > maxBytes) return new AiAttachmentRead.TooLarge(len);
            return new AiAttachmentRead.File(File.ReadAllBytes(path));
        }
        finally
        {
            files.Discard(path);
        }
    }

    private static AiAttachmentRead ReadItemForAi(Outlook.Application app, Outlook.Attachment att, long maxBytes, AiFiles files)
    {
        var path = files.PrepareTempPath(item: true);
        Outlook.NameSpace? ns = null;
        object? opened = null;
        try
        {
            var len = SaveForAi(att, path);
            if (len > maxBytes) return new AiAttachmentRead.TooLarge(len);
            ns = app.Session;
            opened = ns.OpenSharedItem(path);
            if (opened is not Outlook.MailItem mi) return new AiAttachmentRead.Failed("NotAMailItem");
            try
            {
                var subject = ComSafe.Try(() => mi.Subject);
                var name = ComSafe.Try(() => mi.SenderName);
                var address = ComSafe.Try(() => mi.SenderEmailAddress);   // raw, no Exchange resolution
                var sent = OutlookDates.FromLocal(ComSafe.Try(() => mi.SentOn), TimeZoneInfo.Local);
                var body = ComSafe.Try(() => mi.Body);
                var names = new List<string>();
                Outlook.Attachments? inner = null;
                try
                {
                    inner = mi.Attachments;
                    var count = Math.Min(inner.Count, AiAttachmentCom.MaxItemAttachmentNames);
                    for (var i = 1; i <= count; i++)
                    {
                        var a = inner[i];
                        try
                        {
                            names.Add(InlineAttachments.FileNameFor(
                                ComSafe.Try(() => a.FileName), ComSafe.Try(() => a.DisplayName), i));
                        }
                        finally
                        {
                            Release(a);
                        }
                    }
                }
                finally
                {
                    Release(inner);
                }
                return AiAttachmentCom.Item(subject, name, address, sent, body, names);
            }
            finally
            {
                ComSafe.Try(() => { mi.Close(Outlook.OlInspectorClose.olDiscard); return 0; });
            }
        }
        finally
        {
            // Close (above), then release, then discard: Outlook holds the .msg open until released.
            Release(opened);
            Release(ns);
            files.Discard(path);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The Core store retries via Discard.
        }
    }

    private static SavedAttachment SaveOne(Outlook.MailItem item, AttachmentInfo target, string dir)
    {
        // Computed on the worker thread right before SaveAsFile: saves are serialized, so names can't collide.
        var path = FileNames.UniquePath(dir, target.FileName);
        var atts = item.Attachments;
        Outlook.Attachment? att = null;
        try
        {
            att = atts[target.Position];
            att.SaveAsFile(path);
            return new SavedAttachment(path, target.FileName, new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                   && !ComErrors.IsOutlookGone(ex))
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
            throw new McpException($"Could not save \"{target.FileName}\": {ex.Message}");
        }
        finally
        {
            Release(att);
            Release(atts);
        }
    }
}

internal sealed class OutlookAttachmentSaver(OutlookWorker worker, OutlookMail mail, OutlookStores stores) : IAttachmentSaver
{
    public async Task<SavedAttachment> SaveOneAsync(string id, string? filename, int? position, string dir, CancellationToken ct)
    {
        await stores.EnsureResolvedAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
        return await worker.RunAsync(app => mail.SaveAttachment(app, id, filename, position, dir), WorkPriority.Interactive, ct,
            OutlookStores.AttachmentTimeout).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AttachmentSaveOutcome>> SaveManyAsync(
        string id, IReadOnlyList<AttachmentInfo> wanted, string dir, CancellationToken ct)
    {
        await stores.EnsureResolvedAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
        return await worker.RunAsync(app => mail.SaveAttachments(app, id, wanted, dir), WorkPriority.Interactive, ct,
            OutlookStores.AttachmentTimeout).ConfigureAwait(false);
    }
}

internal sealed class OutlookAttachmentLinkSaver(OutlookWorker worker, OutlookMail mail, OutlookStores stores)
    : IAttachmentLinkSaver
{
    public async Task<SavedAttachment> SaveForLinkAsync(string id, string? filename, int? position, string tempPath,
        long maxBytes, CancellationToken ct)
    {
        using var abandon = new CancellationTokenSource();
        try
        {
            await stores.EnsureResolvedAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
            return await worker.RunAsync(
                app => mail.SaveAttachmentForLink(app, id, filename, position, tempPath, maxBytes, abandon.Token),
                WorkPriority.Interactive, ct, OutlookStores.AttachmentTimeout).ConfigureAwait(false);
        }
        catch
        {
            abandon.Cancel();   // before Core's Discard runs: a COM save still running deletes its own file
            throw;
        }
    }
}
