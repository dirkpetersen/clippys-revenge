using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

public enum UpdateCheckOutcome { Ok, NoRelease, Failed }

/// <summary>Ok: Release set, Error null. NoRelease: both null. Failed: Error = an UpdateTexts reason.</summary>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Release, string? Error);

/// <summary>A verified file in the staging folder (StagedFileName of Version).</summary>
public sealed record StagedDownload(SemVersion Version, string Path, string Sha256, long Size)
{
    public StagedUpdate ToStaged() => new(Version.ToString(), Sha256, Size);
}

/// <summary>Message = Reason = an UpdateTexts reason (SPEC 20.10).</summary>
public sealed class UpdateDownloadException : Exception
{
    public UpdateDownloadException(string reason, Exception? inner = null) : base(reason, inner)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>Talks to GitHub (SPEC 20.3, 20.4): latest-release lookup and verified downloads. Only the pinned URLs
/// from <see cref="UpdateEndpoints"/> and redirect targets on the host allow-list are ever requested.</summary>
public sealed class UpdateClient : IDisposable
{
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SumsTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ExeTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    public const int MaxRedirects = 5;
    public const int BufferSize = 81_920;

    private readonly SemVersion _running;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;

    /// <param name="handler">Tests pass a fake (not disposed by the client). Null: the default handler, owned and
    /// disposed by the client.</param>
    /// <param name="logger">Optional.</param>
    /// <exception cref="ArgumentNullException">running is null.</exception>
    public UpdateClient(SemVersion running, HttpMessageHandler? handler = null, TimeProvider? time = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(running);
        _running = running;
        _time = time ?? TimeProvider.System;
        _log = logger;
        var owned = handler is null;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler, disposeHandler: owned) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _http.Dispose();

    // ---- lookup (SPEC 20.3, 20.4) ----

    /// <summary>GET releases/latest. Never throws except OperationCanceledException when ct is cancelled.</summary>
    public async Task<UpdateCheckResult> GetLatestAsync(CancellationToken ct)
    {
        var start = _time.GetTimestamp();
        UpdateCheckResult result;
        using var timeout = new CancellationTokenSource(ApiTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            result = await LookupAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            result = Failed(UpdateTexts.ReasonTimeout);
        }
        catch (Exception ex)
        {
            result = Failed(UpdateTexts.ReasonNetwork(ex.GetType().Name));
        }

        var ms = (long)_time.GetElapsedTime(start).TotalMilliseconds;
        if (result.Outcome == UpdateCheckOutcome.Failed)
        {
            _log?.LogWarning("Update lookup failed: {Reason} in {Ms} ms", result.Error, ms);
        }
        else
        {
            _log?.LogInformation("Update lookup: {Outcome} {Tag} in {Ms} ms", result.Outcome,
                result.Release?.Tag ?? "-", ms);
        }
        return result;
    }

    private static UpdateCheckResult Failed(string reason) => new(UpdateCheckOutcome.Failed, null, reason);

    private async Task<UpdateCheckResult> LookupAsync(CancellationToken ct)
    {
        using var request = NewRequest(new Uri(UpdateEndpoints.LatestReleaseApi), api: true);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        var code = (int)response.StatusCode;
        switch (code)
        {
            case 200:
                break;
            case 404:
                return new UpdateCheckResult(UpdateCheckOutcome.NoRelease, null, null);
            case 403:
            case 429:
                return Failed(UpdateTexts.ReasonRateLimited);
            default:
                return Failed(UpdateTexts.ReasonHttp(code));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var bytes = await ReadCappedAsync(stream, ReleaseParser.MaxJsonBytes, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return Failed(UpdateTexts.ReasonBadJson);
        }
        var parsed = ReleaseParser.Parse(Encoding.UTF8.GetString(bytes));
        return parsed.Release is { } release
            ? new UpdateCheckResult(UpdateCheckOutcome.Ok, release, null)
            : Failed(parsed.Error ?? UpdateTexts.ReasonBadJson);
    }

    // ---- hash (SPEC 20.5) ----

    /// <summary>Downloads SHA256SUMS.txt and returns the expected exe hash (lower-case hex). Checks the asset digest
    /// too, when the release has one.</summary>
    /// <exception cref="UpdateDownloadException">Any failure (reason in Message).</exception>
    /// <exception cref="OperationCanceledException">ct was cancelled.</exception>
    public async Task<string> GetExpectedHashAsync(ReleaseInfo release, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        using var timeout = new CancellationTokenSource(SumsTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var response = await SendFollowingAsync(
                UpdateEndpoints.AssetUrl(release.Tag, UpdateEndpoints.SumsAssetName), linked.Token)
                .ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } len && len > ReleaseParser.MaxSumsBytes)
            {
                throw new UpdateDownloadException(UpdateTexts.ReasonNoAssets);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            var bytes = await ReadCappedAsync(stream, ReleaseParser.MaxSumsBytes, linked.Token).ConfigureAwait(false)
                ?? throw new UpdateDownloadException(UpdateTexts.ReasonNoAssets);
            var hash = Sha256Sums.Find(Encoding.UTF8.GetString(bytes), UpdateEndpoints.ExeAssetName)
                ?? throw new UpdateDownloadException(UpdateTexts.ReasonSumsMissing);
            if (release.ExeDigest is not null && !UpdateHash.Same(release.ExeDigest, hash))
            {
                throw new UpdateDownloadException(UpdateTexts.ReasonHashMismatch);
            }
            return hash;
        }
        catch (UpdateDownloadException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new UpdateDownloadException(UpdateTexts.ReasonDownloadTimeout, ex);
        }
        catch (Exception ex)
        {
            throw new UpdateDownloadException(UpdateTexts.ReasonNetwork(ex.GetType().Name), ex);
        }
    }

    // ---- download (SPEC 20.5) ----

    /// <summary>GetExpectedHashAsync, then the exe to &lt;stagingDir&gt;\ClippysRevenge-&lt;v&gt;.exe.partial, verified,
    /// renamed to ClippysRevenge-&lt;v&gt;.exe (overwriting). progress gets 0-100, only when the value grows.</summary>
    /// <exception cref="UpdateDownloadException">Any failure; the partial and final file are deleted first.</exception>
    /// <exception cref="OperationCanceledException">ct was cancelled; the partial file is deleted.</exception>
    public async Task<StagedDownload> DownloadAsync(ReleaseInfo release, string stagingDir, IProgress<int>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDir);

        var expected = await GetExpectedHashAsync(release, ct).ConfigureAwait(false);

        var fileName = UpdatePaths.StagedFileName(release.Version);
        var partial = Path.Combine(stagingDir, fileName + UpdatePaths.PartialSuffix);
        var final = Path.Combine(stagingDir, fileName);
        try
        {
            try
            {
                Directory.CreateDirectory(stagingDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new UpdateDownloadException(UpdateTexts.ReasonSaveFailed(ex.GetType().Name), ex);
            }
            return await DownloadCoreAsync(release, expected, partial, final, progress, ct).ConfigureAwait(false);
        }
        catch (UpdateDownloadException)
        {
            Cleanup(partial, final);
            throw;
        }
        catch (OperationCanceledException)
        {
            Cleanup(partial, final);
            throw;
        }
    }

    private async Task<StagedDownload> DownloadCoreAsync(ReleaseInfo release, string expected, string partial,
        string final, IProgress<int>? progress, CancellationToken ct)
    {
        var start = _time.GetTimestamp();
        using var overall = new CancellationTokenSource(ExeTimeout, _time);
        using var stall = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, overall.Token, stall.Token);
        var token = linked.Token;
        try
        {
            stall.CancelAfter(StallTimeout);
            using var response = await SendFollowingAsync(
                UpdateEndpoints.AssetUrl(release.Tag, UpdateEndpoints.ExeAssetName), token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } len && len != release.ExeSize)
            {
                throw new UpdateDownloadException(UpdateTexts.ReasonSizeMismatch);
            }

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var head = new byte[2];
            var headCount = 0;
            long count = 0;
            var lastPercent = -1;
            var buffer = new byte[BufferSize];

            await using var read = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            FileStream file;
            try
            {
                file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize,
                    useAsync: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UpdateDownloadException(UpdateTexts.ReasonSaveFailed(ex.GetType().Name), ex);
            }

            await using (file.ConfigureAwait(false))
            {
                while (true)
                {
                    var n = await read.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    stall.CancelAfter(StallTimeout);
                    token.ThrowIfCancellationRequested();
                    if (n == 0)
                    {
                        break;
                    }
                    count += n;
                    if (count > release.ExeSize)
                    {
                        throw new UpdateDownloadException(UpdateTexts.ReasonSizeMismatch);
                    }
                    hasher.AppendData(buffer, 0, n);
                    for (var i = 0; i < n && headCount < 2; i++)
                    {
                        head[headCount++] = buffer[i];
                    }
                    try
                    {
                        await file.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new UpdateDownloadException(UpdateTexts.ReasonSaveFailed(ex.GetType().Name), ex);
                    }
                    var percent = (int)(count * 100 / release.ExeSize);
                    if (percent > lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(percent);
                    }
                }

                if (count != release.ExeSize)
                {
                    throw new UpdateDownloadException(UpdateTexts.ReasonSizeMismatch);
                }
                if (headCount < 2 || head[0] != 0x4D || head[1] != 0x5A)
                {
                    throw new UpdateDownloadException(UpdateTexts.ReasonNotExe);
                }
                var hash = UpdateHash.ToHex(hasher.GetHashAndReset());
                if (!UpdateHash.Same(hash, expected))
                {
                    throw new UpdateDownloadException(UpdateTexts.ReasonHashMismatch);
                }
                try
                {
                    await file.FlushAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new UpdateDownloadException(UpdateTexts.ReasonSaveFailed(ex.GetType().Name), ex);
                }

                expected = hash;
            }

            try
            {
                File.Move(partial, final, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UpdateDownloadException(UpdateTexts.ReasonSaveFailed(ex.GetType().Name), ex);
            }

            _log?.LogInformation("Update {Version} downloaded: {Bytes} bytes in {Ms} ms", release.Version, count,
                (long)_time.GetElapsedTime(start).TotalMilliseconds);
            return new StagedDownload(release.Version, final, expected, count);
        }
        catch (UpdateDownloadException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new UpdateDownloadException(UpdateTexts.ReasonDownloadTimeout, ex);
        }
        catch (Exception ex)
        {
            throw new UpdateDownloadException(UpdateTexts.ReasonNetwork(ex.GetType().Name), ex);
        }
    }

    private void Cleanup(string partial, string final)
    {
        foreach (var path in new[] { partial, final })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.LogDebug("Couldn't delete a staged update file ({Type})", ex.GetType().Name);
            }
        }
    }

    // ---- HTTP plumbing ----

    private HttpRequestMessage NewRequest(Uri uri, bool api)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UpdateEndpoints.UserAgent(_running));
        if (api)
        {
            request.Headers.TryAddWithoutValidation("Accept", UpdateEndpoints.ApiAccept);
            request.Headers.TryAddWithoutValidation(UpdateEndpoints.ApiVersionHeader, UpdateEndpoints.ApiVersion);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Accept", UpdateEndpoints.DownloadAccept);
        }
        return request;
    }

    /// <summary>GETs first, following redirects by hand through the host allow-list. Returns the 200 response.</summary>
    private async Task<HttpResponseMessage> SendFollowingAsync(Uri first, CancellationToken ct)
    {
        var current = first;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            _log?.LogDebug("Update request to {Host}", current.Host);
            using var request = NewRequest(current, api: false);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            var keep = false;
            try
            {
                var code = (int)response.StatusCode;
                switch (code)
                {
                    case 200:
                        keep = true;
                        return response;
                    case 301:
                    case 302:
                    case 303:
                    case 307:
                    case 308:
                        if (hop == MaxRedirects || response.Headers.Location is not { } location)
                        {
                            throw new UpdateDownloadException(UpdateTexts.ReasonHttp(code));
                        }
                        var target = new Uri(current, location);
                        if (!UpdateEndpoints.IsAllowedRedirect(target))
                        {
                            throw new UpdateDownloadException(UpdateTexts.ReasonRedirect(
                                target.IsAbsoluteUri ? target.Host : "(none)"));
                        }
                        current = target;
                        break;
                    case 403:
                    case 429:
                        throw new UpdateDownloadException(UpdateTexts.ReasonRateLimited);
                    default:
                        throw new UpdateDownloadException(UpdateTexts.ReasonHttp(code));
                }
            }
            finally
            {
                if (!keep)
                {
                    response.Dispose();
                }
            }
        }
        throw new UpdateDownloadException(UpdateTexts.ReasonHttp(302));
    }

    /// <summary>Reads up to cap bytes. Null when the stream has more than cap bytes.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream stream, int cap, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[Math.Min(BufferSize, cap + 1)];
        while (true)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return ms.ToArray();
            }
            if (ms.Length + n > cap)
            {
                return null;
            }
            ms.Write(buffer, 0, n);
        }
    }
}
