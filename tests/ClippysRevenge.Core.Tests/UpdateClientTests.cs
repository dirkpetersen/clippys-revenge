using static ClippysRevenge.Core.Tests.GitHubFixture;
using Reply = ClippysRevenge.Core.Tests.FakeGitHubHandler.Reply;

namespace ClippysRevenge.Core.Tests;

public sealed class UpdateClientTests : IDisposable
{
    private static readonly SemVersion Running = SemVersion.Parse("0.0.5");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-m122-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHubHandler _h = new();
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private UpdateClient NewClient() => new(Running, _h, _time);

    private string Staging => Path.Combine(_root, "update");

    private static async Task<UpdateDownloadException> Fails(Task<StagedDownload> task) =>
        await Assert.ThrowsAsync<UpdateDownloadException>(() => task);

    private void ScriptSums(byte[] canonical) =>
        _h.On(SumsUrl(), Reply.Redirect(Cdn)).On(Cdn, Reply.Text(Sums(canonical)));

    private void AssertNoStagedFiles() =>
        Assert.True(!Directory.Exists(Staging) || Directory.GetFiles(Staging).Length == 0);

    // ---- lookup ----

    [Fact]
    public async Task Lookup_ok_sends_only_the_expected_headers()
    {
        _h.On(Api, Reply.Text(ReleaseJson()));
        using var c = NewClient();
        var r = await c.GetLatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Ok, r.Outcome);
        Assert.Equal("v0.0.6", r.Release!.Tag);
        var req = Assert.Single(_h.Requests);
        Assert.Equal(Api, req.Uri);
        Assert.Equal("ClippysRevenge/0.0.5", req.Headers["User-Agent"]);
        Assert.Equal("application/vnd.github+json", req.Headers["Accept"]);
        Assert.Equal("2022-11-28", req.Headers["X-GitHub-Api-Version"]);
        Assert.DoesNotContain("Authorization", req.Headers.Keys);
        Assert.DoesNotContain("Cookie", req.Headers.Keys);
    }

    [Fact]
    public async Task Lookup_ignores_urls_in_the_json()
    {
        _h.On(Api, Reply.Text(ReleaseJson()));
        using var c = NewClient();
        await c.GetLatestAsync(TestContext.Current.CancellationToken);
        Assert.All(_h.Requests, r => Assert.Equal("api.github.com", r.Uri.Host));
        Assert.Empty(_h.Unexpected);
    }

    [Fact]
    public async Task Lookup_404_is_no_release()
    {
        _h.On(Api, Reply.Code(404));
        using var c = NewClient();
        var r = await c.GetLatestAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateCheckOutcome.NoRelease, r.Outcome);
        Assert.Null(r.Release);
        Assert.Null(r.Error);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    public async Task Lookup_rate_limit(int code)
    {
        _h.On(Api, Reply.Code(code));
        using var c = NewClient();
        var r = await c.GetLatestAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateCheckOutcome.Failed, r.Outcome);
        Assert.Equal(UpdateTexts.ReasonRateLimited, r.Error);
    }

    [Fact]
    public async Task Lookup_500_fails_with_the_code()
    {
        _h.On(Api, Reply.Code(500));
        using var c = NewClient();
        var r = await c.GetLatestAsync(TestContext.Current.CancellationToken);
        Assert.Equal("GitHub answered HTTP 500", r.Error);
    }

    [Fact]
    public async Task Lookup_does_not_follow_redirects()
    {
        _h.On(Api, Reply.Redirect("https://github.com/elsewhere"));
        using var c = NewClient();
        var r = await c.GetLatestAsync(TestContext.Current.CancellationToken);
        Assert.Equal("GitHub answered HTTP 302", r.Error);
        Assert.Single(_h.Requests);
    }

    [Fact]
    public async Task Lookup_body_over_one_MiB_is_bad_json()
    {
        _h.On(Api, Reply.Text(new string(' ', ReleaseParser.MaxJsonBytes + 1)));
        using var c = NewClient();
        var r = await c.GetLatestAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateTexts.ReasonBadJson, r.Error);
    }

    [Fact]
    public async Task Lookup_prerelease_is_not_a_full_release()
    {
        _h.On(Api, Reply.Text(ReleaseJson(prerelease: true)));
        using var c = NewClient();
        var r = await c.GetLatestAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateTexts.ReasonNotFullRelease, r.Error);
    }

    [Fact]
    public async Task Lookup_times_out_after_15_seconds()
    {
        _h.On(Api, Reply.Hanging());
        using var c = NewClient();
        var task = c.GetLatestAsync(TestContext.Current.CancellationToken);
        await WaitUntil(() => _h.Requests.Count == 1);
        _time.Advance(UpdateClient.ApiTimeout);
        var r = await task;
        Assert.Equal(UpdateTexts.ReasonTimeout, r.Error);
    }

    [Fact]
    public async Task Lookup_cancellation_by_the_caller_throws()
    {
        _h.On(Api, Reply.Hanging());
        using var c = NewClient();
        using var cts = new CancellationTokenSource();
        var task = c.GetLatestAsync(cts.Token);
        await WaitUntil(() => _h.Requests.Count == 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Lookup_network_exception_reports_the_type_only()
    {
        _h.On(Api, Reply.Throws(new HttpRequestException("secret details")));
        using var c = NewClient();
        var r = await c.GetLatestAsync(TestContext.Current.CancellationToken);
        Assert.Equal("network error (HttpRequestException)", r.Error);
    }

    // ---- download ----

    [Fact]
    public async Task Download_happy_path_follows_redirects_and_verifies()
    {
        var exe = Exe();
        ScriptDownload(_h, exe, chunk: 100_000);
        var progress = new SyncProgress();
        using var c = NewClient();

        var d = await c.DownloadAsync(Release(exe), Staging, progress, TestContext.Current.CancellationToken);

        Assert.Equal(Sha(exe), d.Sha256);
        Assert.Equal(exe.Length, d.Size);
        Assert.Equal(Path.Combine(Staging, "ClippysRevenge-0.0.6.exe"), d.Path);
        Assert.Equal(exe, await File.ReadAllBytesAsync(d.Path, TestContext.Current.CancellationToken));
        Assert.Equal([d.Path], Directory.GetFiles(Staging));
        Assert.Equal(new StagedUpdate("0.0.6", Sha(exe), exe.Length), d.ToStaged());

        Assert.Equal(4, _h.Requests.Count);
        Assert.All(_h.Requests, r =>
        {
            Assert.Equal(["Accept", "User-Agent"], r.Headers.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("application/octet-stream", r.Headers["Accept"]);
            Assert.Equal("ClippysRevenge/0.0.5", r.Headers["User-Agent"]);
        });
        Assert.NotEmpty(progress.Values);
        Assert.Equal(progress.Values.Order().Distinct(), progress.Values);
        Assert.Equal(100, progress.Values[^1]);
    }

    [Fact]
    public async Task Download_follows_a_three_hop_chain_through_the_allow_list()
    {
        var exe = Exe();
        _h.On(SumsUrl(), Reply.Redirect("https://objects.githubusercontent.com/a"));
        _h.On("https://objects.githubusercontent.com/a", Reply.Redirect(Cdn));
        _h.On(Cdn, Reply.Text(Sums(exe)));
        using var c = NewClient();
        var hash = await c.GetExpectedHashAsync(Release(exe), TestContext.Current.CancellationToken);
        Assert.Equal(Sha(exe), hash);
        Assert.Equal(3, _h.Requests.Count);
    }

    [Fact]
    public async Task Download_sixth_redirect_fails_with_the_http_code()
    {
        var exe = Exe();
        _h.On(SumsUrl(), Reply.Redirect("https://release-assets.githubusercontent.com/1"));
        for (var i = 1; i <= 5; i++)
        {
            _h.On($"https://release-assets.githubusercontent.com/{i}",
                Reply.Redirect($"https://release-assets.githubusercontent.com/{i + 1}"));
        }
        using var c = NewClient();
        var ex = await Assert.ThrowsAsync<UpdateDownloadException>(
            () => c.GetExpectedHashAsync(Release(exe), TestContext.Current.CancellationToken));
        Assert.Equal(UpdateTexts.ReasonHttp(302), ex.Reason);
        Assert.False(_h.Requested("https://release-assets.githubusercontent.com/6"));
    }

    [Fact]
    public async Task Download_redirect_to_another_host_is_refused_and_never_requested()
    {
        var exe = Exe();
        _h.On(SumsUrl(), Reply.Redirect("https://evil.example/x"));
        using var c = NewClient();
        var ex = await Assert.ThrowsAsync<UpdateDownloadException>(
            () => c.GetExpectedHashAsync(Release(exe), TestContext.Current.CancellationToken));
        Assert.Equal(UpdateTexts.ReasonRedirect("evil.example"), ex.Reason);
        Assert.False(_h.Requested("https://evil.example/x"));
    }

    [Fact]
    public async Task Download_redirect_to_http_is_refused()
    {
        var exe = Exe();
        _h.On(SumsUrl(), Reply.Redirect("http://release-assets.githubusercontent.com/x"));
        using var c = NewClient();
        var ex = await Assert.ThrowsAsync<UpdateDownloadException>(
            () => c.GetExpectedHashAsync(Release(exe), TestContext.Current.CancellationToken));
        Assert.Equal(UpdateTexts.ReasonRedirect("release-assets.githubusercontent.com"), ex.Reason);
        Assert.Single(_h.Requests);
    }

    [Fact]
    public async Task Download_relative_location_resolves_against_github()
    {
        var exe = Exe();
        _h.On(SumsUrl(), Reply.Redirect("/a/b"));
        _h.On("https://github.com/a/b", Reply.Text(Sums(exe)));
        using var c = NewClient();
        Assert.Equal(Sha(exe), await c.GetExpectedHashAsync(Release(exe), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(404, "GitHub answered HTTP 404")]
    [InlineData(429, "GitHub's rate limit was reached; try again later")]
    public async Task Download_error_statuses_have_reasons(int code, string reason)
    {
        _h.On(SumsUrl(), Reply.Code(code));
        using var c = NewClient();
        var ex = await Assert.ThrowsAsync<UpdateDownloadException>(
            () => c.GetExpectedHashAsync(Release(Exe()), TestContext.Current.CancellationToken));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(reason, ex.Message);
    }

    [Fact]
    public async Task Sums_without_an_exe_line_is_missing()
    {
        _h.On(SumsUrl(), Reply.Text(Sha(Exe()) + "  other.exe\n"));
        using var c = NewClient();
        var ex = await Assert.ThrowsAsync<UpdateDownloadException>(
            () => c.GetExpectedHashAsync(Release(Exe()), TestContext.Current.CancellationToken));
        Assert.Equal(UpdateTexts.ReasonSumsMissing, ex.Reason);
    }

    [Fact]
    public async Task Sums_over_64_KiB_by_content_length_is_refused()
    {
        _h.On(SumsUrl(), Reply.Text(new string('a', ReleaseParser.MaxSumsBytes + 1)));
        using var c = NewClient();
        var ex = await Assert.ThrowsAsync<UpdateDownloadException>(
            () => c.GetExpectedHashAsync(Release(Exe()), TestContext.Current.CancellationToken));
        Assert.Equal(UpdateTexts.ReasonNoAssets, ex.Reason);
    }

    [Fact]
    public async Task Sums_over_64_KiB_in_the_body_is_refused_whatever_the_header_says()
    {
        _h.On(SumsUrl(), Reply.Ok(new byte[ReleaseParser.MaxSumsBytes + 1], contentLength: 10));
        using var c = NewClient();
        var ex = await Assert.ThrowsAsync<UpdateDownloadException>(
            () => c.GetExpectedHashAsync(Release(Exe()), TestContext.Current.CancellationToken));
        Assert.Equal(UpdateTexts.ReasonNoAssets, ex.Reason);
    }

    [Fact]
    public async Task Digest_mismatch_fails_before_any_exe_request()
    {
        var exe = Exe();
        ScriptSums(exe);
        using var c = NewClient();
        var ex = await Fails(c.DownloadAsync(Release(exe, digest: new string('a', 64)), Staging, null,
            TestContext.Current.CancellationToken));
        Assert.Equal(UpdateTexts.ReasonHashMismatch, ex.Reason);
        Assert.False(_h.Requested(ExeUrl().AbsoluteUri));
        Assert.Empty(_h.Unexpected);
    }

    [Fact]
    public async Task Digest_that_matches_is_accepted()
    {
        var exe = Exe();
        ScriptDownload(_h, exe);
        using var c = NewClient();
        var d = await c.DownloadAsync(Release(exe, digest: Sha(exe)), Staging, null, TestContext.Current.CancellationToken);
        Assert.Equal(Sha(exe), d.Sha256);
    }

    private async Task<UpdateDownloadException> DownloadServing(byte[] served, long? contentLength = null)
    {
        var canonical = Exe();
        ScriptSums(canonical);
        _h.On(ExeUrl(), Reply.Redirect(Cdn2)).On(Cdn2, Reply.Ok(served, contentLength: contentLength));
        using var c = NewClient();
        var ex = await Fails(c.DownloadAsync(Release(canonical), Staging, null, TestContext.Current.CancellationToken));
        AssertNoStagedFiles();
        return ex;
    }

    [Fact]
    public async Task Exe_one_byte_short_is_a_size_mismatch()
    {
        var ex = await DownloadServing(Exe()[..^1]);
        Assert.Equal(UpdateTexts.ReasonSizeMismatch, ex.Reason);
    }

    [Fact]
    public async Task Exe_one_byte_long_is_a_size_mismatch()
    {
        var ex = await DownloadServing([.. Exe(), 0]);
        Assert.Equal(UpdateTexts.ReasonSizeMismatch, ex.Reason);
    }

    [Fact]
    public async Task Exe_content_length_that_differs_fails_before_reading()
    {
        var ex = await DownloadServing(Exe(), contentLength: 5_000);
        Assert.Equal(UpdateTexts.ReasonSizeMismatch, ex.Reason);
    }

    [Fact]
    public async Task Exe_without_MZ_is_not_an_exe()
    {
        var bad = Exe();
        bad[0] = (byte)'P';
        var ex = await DownloadServing(bad);
        Assert.Equal(UpdateTexts.ReasonNotExe, ex.Reason);
    }

    [Fact]
    public async Task Exe_with_one_flipped_byte_has_a_wrong_hash()
    {
        var bad = Exe();
        bad[5000] ^= 1;
        var ex = await DownloadServing(bad);
        Assert.Equal(UpdateTexts.ReasonHashMismatch, ex.Reason);
    }

    [Fact]
    public async Task Download_stall_times_out_and_deletes_the_partial()
    {
        var exe = Exe();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptDownload(_h, exe, chunk: 262_144, beforeChunk: async (i, ct) =>
        {
            if (i != 1) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        using var c = NewClient();
        var task = c.DownloadAsync(Release(exe), Staging, null, TestContext.Current.CancellationToken);
        await entered.Task;
        _time.Advance(UpdateClient.StallTimeout);
        var ex = await Fails(task);
        Assert.Equal(UpdateTexts.ReasonDownloadTimeout, ex.Reason);
        AssertNoStagedFiles();
    }

    [Fact]
    public async Task Download_over_ten_minutes_times_out_even_without_a_stall()
    {
        var exe = Exe();
        ScriptDownload(_h, exe, chunk: 65_536, beforeChunk: (_, _) =>
        {
            _time.Advance(TimeSpan.FromSeconds(59));
            return Task.CompletedTask;
        });
        using var c = NewClient();
        var ex = await Fails(c.DownloadAsync(Release(exe), Staging, null, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateTexts.ReasonDownloadTimeout, ex.Reason);
        AssertNoStagedFiles();
    }

    [Fact]
    public async Task Download_cancelled_by_the_caller_deletes_the_partial()
    {
        var exe = Exe();
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptDownload(_h, exe, chunk: 262_144, beforeChunk: async (i, ct) =>
        {
            if (i != 1) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        using var c = NewClient();
        var task = c.DownloadAsync(Release(exe), Staging, null, cts.Token);
        await entered.Task;
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        AssertNoStagedFiles();
    }

    [Fact]
    public async Task Download_into_a_staging_path_that_is_a_file_fails_to_save()
    {
        var exe = Exe();
        ScriptDownload(_h, exe);
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocker");
        await File.WriteAllTextAsync(blocker, "x", TestContext.Current.CancellationToken);
        using var c = NewClient();
        var ex = await Fails(c.DownloadAsync(Release(exe), blocker, null, TestContext.Current.CancellationToken));
        Assert.StartsWith("the file couldn't be saved (", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("os", "windows")]
    public async Task Download_with_a_read_only_partial_fails_to_save()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var exe = Exe();
        ScriptDownload(_h, exe);
        Directory.CreateDirectory(Staging);
        var partial = Path.Combine(Staging, "ClippysRevenge-0.0.6.exe.partial");
        await File.WriteAllTextAsync(partial, "x", TestContext.Current.CancellationToken);
        File.SetAttributes(partial, FileAttributes.ReadOnly);
        using var c = NewClient();
        var ex = await Fails(c.DownloadAsync(Release(exe), Staging, null, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateTexts.ReasonSaveFailed("UnauthorizedAccessException"), ex.Reason);
    }

    [Fact]
    public async Task Download_overwrites_an_existing_staged_file()
    {
        var exe = Exe();
        ScriptDownload(_h, exe);
        Directory.CreateDirectory(Staging);
        var final = Path.Combine(Staging, "ClippysRevenge-0.0.6.exe");
        await File.WriteAllTextAsync(final, "old", TestContext.Current.CancellationToken);
        using var c = NewClient();
        await c.DownloadAsync(Release(exe), Staging, null, TestContext.Current.CancellationToken);
        Assert.Equal(exe, await File.ReadAllBytesAsync(final, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Client_does_not_dispose_a_handler_it_was_given()
    {
        var c = NewClient();
        c.Dispose();
        _h.On(Api, Reply.Text(ReleaseJson()));
        using var other = NewClient();
        Assert.Equal(UpdateCheckOutcome.Ok, (await other.GetLatestAsync(TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public void Constructor_rejects_null_version()
    {
        Assert.Throws<ArgumentNullException>(() => new UpdateClient(null!));
    }
}
