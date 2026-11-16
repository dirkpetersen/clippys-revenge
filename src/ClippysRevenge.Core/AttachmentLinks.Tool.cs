using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

public interface IAttachmentLinkSaver
{
    /// <summary>Live (worker thread, Interactive): resolve the email by id (same chain as get_attachment), select one
    /// attachment as SPEC 11.3 (AttachmentSelection.SelectOne), refuse when its Size > maxBytes, SaveAsFile(tempPath),
    /// refuse when the saved length > maxBytes (file deleted). Returns Path = tempPath, FileName = the attachment's
    /// original name, Size = bytes on disk.</summary>
    /// <exception cref="AttachmentLinkException">TooBig (LinkTexts.TooBig(n)), before or after the save.</exception>
    /// <exception cref="McpException">Item gone, bad selection, save failed, Outlook unavailable (existing types/texts).</exception>
    /// <exception cref="OperationCanceledException">ct cancelled.</exception>
    Task<SavedAttachment> SaveForLinkAsync(string id, string? filename, int? position, string tempPath, long maxBytes,
        CancellationToken ct);
}

/// <summary>attachment_link (SPEC 24.1-24.3). Never logs the token, url, path, id or file name.</summary>
public sealed class AttachmentLinkTool
{
    private readonly AttachmentLinks _links;
    private readonly IAttachmentLinkSaver _saver;
    private readonly Func<int> _port;
    private readonly TimeZoneInfo _tz;
    private readonly ILogger? _log;

    /// <param name="port">The port the MCP host listens on, read on every call.</param>
    /// <exception cref="ArgumentNullException">links, saver or port is null.</exception>
    public AttachmentLinkTool(AttachmentLinks links, IAttachmentLinkSaver saver, Func<int> port,
        TimeZoneInfo? timeZone = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(saver);
        ArgumentNullException.ThrowIfNull(port);
        _links = links;
        _saver = saver;
        _port = port;
        _tz = timeZone ?? TimeZoneInfo.Local;
        _log = logger;
    }

    /// <exception cref="McpException">"id is required. Use the id from search_emails." and anything the saver throws.</exception>
    /// <exception cref="AttachmentLinkException">Every SPEC 24.2/24.3 refusal with its pinned text.</exception>
    /// <exception cref="OperationCanceledException">ct cancelled.</exception>
    public async Task<AttachmentLinkResult> CreateAsync(string? id, string? filename, int? position, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new McpException("id is required. Use the id from search_emails.");

        try
        {
            _links.CheckCanCreate();
            var tempPath = _links.PrepareTempPath();

            SavedAttachment saved;
            try
            {
                saved = await _saver.SaveForLinkAsync(id.Trim(), filename, position, tempPath,
                    AttachmentLinks.MaxLinkBytes, ct).ConfigureAwait(false);
            }
            catch
            {
                _links.Discard(tempPath);
                throw;
            }

            var link = _links.Add(tempPath, saved.FileName, saved.Size);
            AttachmentLinkResult result;
            try
            {
                result = AttachmentLinkResult.Create(link, _port(), _tz);
            }
            catch
            {
                _links.TryConsume(link.Token);
                _links.Complete(link);
                throw;
            }

            _log?.LogDebug("attachment_link: link created ({Bytes} bytes, {Open} open).", saved.Size, _links.OpenCount);
            return result;
        }
        catch (AttachmentLinkException ex)
        {
            _log?.LogDebug("attachment_link refused ({Reason}).", ex.Reason);
            throw;
        }
    }
}
