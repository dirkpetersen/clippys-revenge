using System.Text;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ClippysRevenge.Core.Tests;

internal sealed class CaptureLogger : ILogger
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Lines)
        {
            Lines.Add(logLevel + "|" + formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> kv)
                foreach (var p in kv) Lines.Add("state|" + p.Key + "=" + p.Value);
            if (exception is not null) Lines.Add("exception|" + exception);
        }
    }
}

public class DraftAttachmentsCollectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-dac-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new();
    private readonly FakeAttachmentSource _src = new();

    public DraftAttachmentsCollectTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private SyncedRoot TempRoot() =>
        new("https://contoso.sharepoint.com/sites/x/Shared Documents/", _dir, "teamsite", false);

    private DraftAttachments Make(ILogger? logger = null,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, ExtractedText>? extract = null,
        Func<MappedLink.Local, long, CancellationToken, SyncedFileResult>? readSynced = null) =>
        new(_src, _clock, logger, extract, readSynced);

    private static AiAttachmentRead.File Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    private static ExtractedText Read(string text) => new(ExtractStatus.Read, text, null, false);

    [Fact]
    public async Task Happy_path()
    {
        _src.Returns("A", 1, Bytes("hello")).Returns("T1", 1, Bytes("world"));
        var req = AttFx.Req(anchor: [AttFx.A(1, "a.txt")], thread: [AttFx.Th("T1", 10, atts: [AttFx.A(1, "b.txt")])]);

        var set = await Make().CollectAsync(req, CancellationToken.None);

        Assert.Equal(["a.txt", "b.txt"], set.Items.Select(i => i.Name).ToArray());
        Assert.Equal(["hello", "world"], set.Items.Select(i => i.Text).ToArray());
        Assert.All(set.Items, i => Assert.Equal(ExtractStatus.Read, i.Status));
        Assert.All(set.Items, i => Assert.Equal(AttachmentKind.File, i.Kind));
        Assert.Equal([AttachmentOrigin.Email, AttachmentOrigin.Thread], set.Items.Select(i => i.Origin).ToArray());
        Assert.Empty(set.Notes);
        Assert.Equal(2, set.Counts.Read);
        Assert.Equal(0, set.Counts.Skipped);
        Assert.Equal(10, set.Counts.Chars);
        Assert.Equal(1, _src.RootsCalls);
    }

    [Fact]
    public async Task Empty_plan_never_reads()
    {
        var set = await Make().CollectAsync(AttFx.Req(), CancellationToken.None);
        Assert.Empty(set.Items);
        Assert.Empty(set.Notes);
        Assert.Equal(0, _src.ReadCalls);
        Assert.Equal(1, _src.RootsCalls);
    }

    [Fact]
    public async Task Items_are_read_and_extracted_one_at_a_time()
    {
        foreach (var i in new[] { 1, 2, 3 }) _src.Returns("A", i, Bytes("x"));
        var col = Make(extract: (n, b, t) =>
        {
            lock (_src.Log) _src.Log.Add("extract " + n);
            return Read("x");
        });
        await col.CollectAsync(AttFx.Req(anchor: [AttFx.A(1, "a.txt"), AttFx.A(2, "b.txt"), AttFx.A(3, "c.txt")]),
            CancellationToken.None);
        Assert.Equal(["read A/1", "extract a.txt", "read A/2", "extract b.txt", "read A/3", "extract c.txt"],
            _src.Log.ToArray());
        Assert.Equal(1, _src.MaxInFlight);
    }

    [Fact]
    public async Task Time_budget_skips_the_rest()
    {
        foreach (var i in new[] { 1, 2, 3, 4 }) _src.Returns("A", i, Bytes("x"));
        _src.OnRead = () => _clock.Advance(TimeSpan.FromSeconds(8));
        var set = await Make().CollectAsync(
            AttFx.Req(anchor: Enumerable.Range(1, 4).Select(i => AttFx.A(i, $"f{i}.txt")).ToList()), CancellationToken.None);

        Assert.Equal(3, _src.ReadCalls);
        Assert.Equal(AttachmentReasons.OutOfTime, set.Items[3].Reason);
        Assert.Equal(ExtractStatus.Skipped, set.Items[3].Status);
        Assert.Equal(new AttachmentNote(AttachmentNoteKind.TookTooLong, "f4.txt", false), Assert.Single(set.Notes));
    }

    [Fact]
    public async Task Per_file_timeout()
    {
        _src.Returns("A", 1, Bytes("x")).Returns("A", 2, Bytes("y"));
        using var started = new ManualResetEventSlim();
        using var sawCancel = new ManualResetEventSlim();
        var col = Make(extract: (n, b, t) =>
        {
            if (n != "slow.txt") return Read("fine");
            started.Set();
            t.WaitHandle.WaitOne(5000);
            if (t.IsCancellationRequested) sawCancel.Set();
            return ExtractedText.Skip(AttachmentReasons.Damaged);
        });

        var task = col.CollectAsync(AttFx.Req(anchor: [AttFx.A(1, "slow.txt"), AttFx.A(2, "next.txt")]), CancellationToken.None);
        Assert.True(await Task.Run(() => started.Wait(5000), TestContext.Current.CancellationToken));
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }
        var set = await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(AttachmentReasons.TookTooLong, set.Items[0].Reason);
        Assert.Equal(ExtractStatus.Read, set.Items[1].Status);
        Assert.Equal("fine", set.Items[1].Text);
        Assert.Equal(new AttachmentNote(AttachmentNoteKind.TookTooLong, "slow.txt", false), Assert.Single(set.Notes));
        Assert.True(sawCancel.Wait(5000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task One_abandoned_pdf_extraction_at_a_time()
    {
        _src.Returns("A", 1, Bytes("x")).Returns("A", 2, Bytes("y"));
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var pdfCalls = 0;
        var col = Make(extract: (n, b, t) =>
        {
            if (!n.EndsWith(".pdf", StringComparison.Ordinal)) return Read("fine");
            Interlocked.Increment(ref pdfCalls);
            started.Set();
            release.Wait();                      // ignores the token, like a stuck PDF parser
            return Read("late");
        });

        try
        {
            var task = col.CollectAsync(AttFx.Req(anchor: [AttFx.A(1, "stuck.pdf")]), CancellationToken.None);
            Assert.True(await Task.Run(() => started.Wait(5000), TestContext.Current.CancellationToken));
            for (var i = 0; i < 100 && !task.IsCompleted; i++)
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(30, TestContext.Current.CancellationToken);
            }
            var first = await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(AttachmentReasons.TookTooLong, first.Items[0].Reason);
            Assert.True(DraftAttachments.PdfAbandoned);

            // A second PDF is skipped without being extracted; other formats still work.
            var second = await Make(extract: (n, b, t) =>
            {
                Interlocked.Increment(ref pdfCalls);
                return Read("fine");
            }).CollectAsync(AttFx.Req(anchor: [AttFx.A(1, "next.pdf"), AttFx.A(2, "ok.txt")]), CancellationToken.None);
            Assert.Equal(AttachmentReasons.TookTooLong, second.Items.Single(i => i.Name == "next.pdf").Reason);
            Assert.Equal(ExtractStatus.Read, second.Items.Single(i => i.Name == "ok.txt").Status);
            Assert.Equal(2, Volatile.Read(ref pdfCalls));          // the stuck one plus ok.txt, not next.pdf
        }
        finally
        {
            release.Set();
            for (var i = 0; i < 200 && DraftAttachments.PdfAbandoned; i++)
                await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        Assert.False(DraftAttachments.PdfAbandoned);
    }

    [Fact]
    public async Task Gone_earlier_email_is_read_once_and_noted_for_tier_2_only()
    {
        _src.Gone("T1");
        var req = AttFx.Req(body: "please see a.pdf",
            thread: [AttFx.Th("T1", 10, atts: [AttFx.A(1, "a.pdf"), AttFx.A(2, "b.pdf")])]);
        var set = await Make().CollectAsync(req, CancellationToken.None);

        Assert.Equal(1, _src.ReadCalls);
        Assert.All(set.Items, i => Assert.Equal(AttachmentReasons.EarlierEmailGone, i.Reason));
        Assert.Equal(new AttachmentNote(AttachmentNoteKind.EarlierEmailGone, "a.pdf", false), Assert.Single(set.Notes));
    }

    [Fact]
    public async Task Gone_anchor_gives_not_on_email_and_no_note()
    {
        _src.Gone("A");
        var set = await Make().CollectAsync(AttFx.Req(anchor: [AttFx.A(1, "a.pdf"), AttFx.A(2, "b.pdf")]), CancellationToken.None);
        Assert.Equal(1, _src.ReadCalls);
        Assert.All(set.Items, i => Assert.Equal(AttachmentReasons.NotOnEmail, i.Reason));
        Assert.Empty(set.Notes);
    }

    [Fact]
    public async Task Source_results_map_to_reasons_and_notes()
    {
        var log = new CaptureLogger();
        _src.Returns("A", 1, new AiAttachmentRead.NotOnEmail())
            .Returns("A", 2, new AiAttachmentRead.TooLarge(30L * 1024 * 1024))
            .Returns("A", 3, new AiAttachmentRead.Failed("COMException"));
        var set = await Make(log).CollectAsync(
            AttFx.Req(anchor: [AttFx.A(1, "a.pdf"), AttFx.A(2, "b.pdf"), AttFx.A(3, "c.pdf")]), CancellationToken.None);

        Assert.Equal([AttachmentReasons.NotOnEmail, AttachmentReasons.TooLarge, AttachmentReasons.Damaged],
            set.Items.Select(i => i.Reason).ToArray());
        Assert.Equal([AttachmentNoteKind.TooLarge, AttachmentNoteKind.Damaged], set.Notes.Select(n => n.Kind).ToArray());
        var warning = Assert.Single(log.Lines, l => l.StartsWith("Warning|", StringComparison.Ordinal));
        Assert.Contains("COMException", warning);
        Assert.DoesNotContain("c.pdf", string.Concat(log.Lines));
    }

    [Fact]
    public async Task Unexpected_exception_gives_damaged()
    {
        var log = new CaptureLogger();
        _src.Throws("A", 1, new InvalidOperationException("secret detail"));
        var set = await Make(log).CollectAsync(AttFx.Req(anchor: [AttFx.A(1, "a.pdf")]), CancellationToken.None);
        Assert.Equal(AttachmentReasons.Damaged, set.Items[0].Reason);
        Assert.Contains("InvalidOperationException", string.Concat(log.Lines));
        Assert.DoesNotContain("secret detail", string.Concat(log.Lines));
    }

    [Fact]
    public async Task Oversized_bytes_are_too_large_without_extraction()
    {
        var called = false;
        _src.Returns("A", 1, new AiAttachmentRead.File(new byte[AttachmentText.MaxFileBytes + 1]));
        var set = await Make(extract: (n, b, t) => { called = true; return Read("x"); })
            .CollectAsync(AttFx.Req(anchor: [AttFx.A(1, "a.pdf")]), CancellationToken.None);
        Assert.Equal(AttachmentReasons.TooLarge, set.Items[0].Reason);
        Assert.False(called);
    }

    [Fact]
    public async Task Attached_item_is_rendered_in_the_local_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        _src.Returns("A", 1, new AiAttachmentRead.Item("Hello", "Ann <ann@x.org>",
            new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero), "body text", ["f.pdf"]));
        var set = await Make().CollectAsync(AttFx.Req(anchor: [AttFx.A(1, "Fwd.msg")], zone: zone), CancellationToken.None);

        var item = Assert.Single(set.Items);
        Assert.Equal(AttachmentKind.Email, item.Kind);
        Assert.Equal(ExtractStatus.Read, item.Status);
        Assert.StartsWith("Subject: Hello\nFrom: Ann <ann@x.org>\nDate: 2026-06-15 14:00", item.Text);
    }

    [Fact]
    public async Task Cloud_attachment_checks()
    {
        File.WriteAllText(Path.Combine(_dir, "hello.txt"), "hello");
        _src.Roots = [TempRoot()];
        _src.Returns("A", 1, new AiAttachmentRead.Cloud(null, "Shared A"))
            .Returns("A", 2, new AiAttachmentRead.Cloud("https://www.dropbox.com/s/x/a.docx", "Shared B"))
            .Returns("A", 4, new AiAttachmentRead.Cloud(AttFx.Share + "hello.txt", "Shared D"));
        var set = await Make().CollectAsync(
            AttFx.Req(anchor: [AttFx.A(1, "c1.docx"), AttFx.A(2, "c2.docx"), AttFx.A(4, "c4.docx")]), CancellationToken.None);

        Assert.All(set.Items, i => Assert.Equal(AttachmentKind.Cloud, i.Kind));
        Assert.Equal(AttachmentReasons.NotSynced, set.Items[0].Reason);
        Assert.Equal(AttachmentReasons.NotMicrosoftLink, set.Items[1].Reason);
        Assert.Equal(ExtractStatus.Read, set.Items[2].Status);
        Assert.Equal("Shared D", set.Items[2].Name);
        Assert.Equal("hello", set.Items[2].Text);
        Assert.Equal(new AttachmentNote(AttachmentNoteKind.NotSynced, "Shared A", true), Assert.Single(set.Notes));
        Assert.Equal(3, set.Counts.Links);
    }

    [Fact]
    public async Task Cloud_attachment_from_outside_is_refused()
    {
        _src.Roots = [TempRoot()];
        _src.Returns("A", 1, new AiAttachmentRead.Cloud(AttFx.Share + "hello.txt", "Shared C"));
        var set = await Make().CollectAsync(
            AttFx.Req(anchor: [AttFx.A(1, "c3.docx")], senderEmail: "eve@other.org"), CancellationToken.None);
        Assert.Equal(AttachmentReasons.FromOutside, set.Items[0].Reason);
        Assert.Equal(new AttachmentNote(AttachmentNoteKind.FromOutside, "Shared C", true), Assert.Single(set.Notes));
    }

    [Fact]
    public async Task Cloud_duplicate_of_a_walked_link_is_dropped()
    {
        File.WriteAllText(Path.Combine(_dir, "hello.txt"), "hello");
        _src.Roots = [TempRoot()];
        _src.Returns("T1", 1, new AiAttachmentRead.Cloud(AttFx.Share + "hello.txt", "Shared"));
        var req = AttFx.Req(body: AttFx.Share + "hello.txt",
            thread: [AttFx.Th("T1", 10, atts: [AttFx.A(1, "shared.docx")])]);
        var set = await Make().CollectAsync(req, CancellationToken.None);

        var item = Assert.Single(set.Items);
        Assert.Equal(AttachmentKind.Link, item.Kind);
        Assert.Equal(1, set.Counts.Links);
        Assert.Equal(1, _src.ReadCalls);
    }

    [Fact]
    public async Task Link_is_dropped_after_a_cloud_attachment_of_the_same_file()
    {
        File.WriteAllText(Path.Combine(_dir, "hello.txt"), "hello");
        _src.Roots = [TempRoot()];
        _src.Returns("A", 1, new AiAttachmentRead.Cloud(AttFx.Share + "hello.txt", "Shared"));
        var set = await Make().CollectAsync(
            AttFx.Req(anchor: [AttFx.A(1, "shared.docx")], body: AttFx.Share + "hello.txt"), CancellationToken.None);
        Assert.Equal(AttachmentKind.Cloud, Assert.Single(set.Items).Kind);
    }

    [Fact]
    public async Task Link_is_read_from_the_synced_folder()
    {
        File.WriteAllText(Path.Combine(_dir, "hello.txt"), "hello");
        _src.Roots = [TempRoot()];
        var set = await Make().CollectAsync(AttFx.Req(body: AttFx.Share + "hello.txt"), CancellationToken.None);
        var item = Assert.Single(set.Items);
        Assert.Equal(AttachmentKind.Link, item.Kind);
        Assert.Equal("hello", item.Text);
        Assert.Equal(0, _src.ReadCalls);
    }

    [Fact]
    public async Task Missing_synced_file_is_not_synced_with_a_note()
    {
        _src.Roots = [TempRoot()];
        var set = await Make().CollectAsync(AttFx.Req(body: AttFx.Share + "missing.txt"), CancellationToken.None);
        Assert.Equal(AttachmentReasons.NotSynced, Assert.Single(set.Items).Reason);
        var note = Assert.Single(set.Notes);
        Assert.Equal(AttachmentNoteKind.NotSynced, note.Kind);
        Assert.True(note.Shared);
    }

    [Fact]
    public async Task Failed_synced_read_is_damaged()
    {
        _src.Roots = [TempRoot()];
        var set = await Make(readSynced: (l, m, t) => new SyncedFileResult.Failed("IOException"))
            .CollectAsync(AttFx.Req(body: AttFx.Share + "hello.txt"), CancellationToken.None);
        Assert.Equal(AttachmentReasons.Damaged, Assert.Single(set.Items).Reason);
    }

    [Fact]
    public async Task Outlook_going_away_skips_the_rest_but_not_preskipped_items()
    {
        _src.Roots = [TempRoot()];
        _src.Returns("A", 1, Bytes("one"));
        _src.Throws("A", 2, new OutlookUnavailableException());
        var req = AttFx.Req(
            anchor: [AttFx.A(1, "a.txt"), AttFx.A(2, "b.txt")],
            body: AttFx.Share + "l.txt",
            thread: [AttFx.Th("T1", 10, atts: [AttFx.A(1, "t.txt"), AttFx.A(2, "pic.png")])]);
        var set = await Make().CollectAsync(req, CancellationToken.None);

        Assert.Equal(ExtractStatus.Read, set.Items[0].Status);
        Assert.Equal(2, _src.ReadCalls);
        Assert.Equal(AttachmentReasons.OutlookNotRunning, set.Items[1].Reason);
        Assert.Equal(AttachmentReasons.OutlookNotRunning, set.Items[2].Reason);
        Assert.Equal(AttachmentKind.Link, set.Items[2].Kind);
        Assert.Equal(AttachmentReasons.OutlookNotRunning, set.Items[3].Reason);
        Assert.Equal(AttachmentReasons.Image, set.Items[4].Reason);
        Assert.Empty(set.Notes);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        _src.Returns("A", 1, Bytes("x")).Returns("A", 2, Bytes("y"));
        _src.OnRead = () => { if (_src.ReadCalls == 2) cts.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Make().CollectAsync(AttFx.Req(anchor: [AttFx.A(1, "a.txt"), AttFx.A(2, "b.txt")]), cts.Token));
    }

    [Fact]
    public async Task Budget_truncates_through_collect()
    {
        foreach (var i in new[] { 1, 2, 3 }) _src.Returns("A", i, Bytes("z"));
        var set = await Make(extract: (n, b, t) => Read(new string('x', 30_000)))
            .CollectAsync(AttFx.Req(anchor: [AttFx.A(1, "a.txt"), AttFx.A(2, "b.txt"), AttFx.A(3, "c.txt")]),
                CancellationToken.None);

        Assert.All(set.Items, i =>
        {
            Assert.Equal(ExtractStatus.Truncated, i.Status);
            Assert.Equal(13_333, i.Text.Length);
            Assert.EndsWith("\u2026", i.Text);
        });
        Assert.Equal(3, set.Notes.Count(n => n.Kind == AttachmentNoteKind.Truncated));
        Assert.True(set.Counts.Chars <= AttachmentBudget.MaxTotalChars);
        Assert.Equal(3, set.Counts.Truncated);
    }

    [Fact]
    public async Task Budget_skips_items_that_do_not_fit()
    {
        _src.Returns("A", 1, Bytes("z")).Returns("T1", 1, Bytes("z"));
        var set = await Make(extract: (n, b, t) => Read(new string('x', n == "a.txt" ? 39_000 : 5_000)))
            .CollectAsync(AttFx.Req(anchor: [AttFx.A(1, "a.txt")], body: "see b.txt",
                thread: [AttFx.Th("T1", 10, atts: [AttFx.A(1, "b.txt")])]), CancellationToken.None);
        Assert.Equal(ExtractStatus.Read, set.Items[0].Status);
        Assert.Equal(AttachmentReasons.TooMuchText, set.Items[1].Reason);
        Assert.Equal("", set.Items[1].Text);
        Assert.Equal(new AttachmentNote(AttachmentNoteKind.TooMuch, "b.txt", false), Assert.Single(set.Notes));
    }

    [Fact]
    public async Task Unreadable_formats_need_no_read()
    {
        var set = await Make().CollectAsync(
            AttFx.Req(anchor: [AttFx.A(1, "photo.jpg"), AttFx.A(2, "old.doc")]), CancellationToken.None);
        Assert.Equal(0, _src.ReadCalls);
        Assert.Equal(new AttachmentNote(AttachmentNoteKind.Format, "old.doc", false, "doc"), Assert.Single(set.Notes));
        Assert.Equal(2, set.Counts.Skipped);
    }

    [Fact]
    public async Task Tier_3_problems_give_no_notes()
    {
        _src.Returns("T1", 1, new AiAttachmentRead.TooLarge(99));
        var set = await Make().CollectAsync(
            AttFx.Req(thread: [AttFx.Th("T1", 10, atts: [AttFx.A(1, "x.pdf")])]), CancellationToken.None);
        Assert.Equal(AttachmentReasons.TooLarge, Assert.Single(set.Items).Reason);
        Assert.Empty(set.Notes);
    }

    [Fact]
    public async Task Roots_failure_is_survived_and_logs_only_the_type()
    {
        var log = new CaptureLogger();
        _src.RootsError = new InvalidOperationException("C:\\secret\\path");
        _src.Returns("A", 1, Bytes("hello"));
        var set = await Make(log).CollectAsync(AttFx.Req(anchor: [AttFx.A(1, "secret-report.txt")]), CancellationToken.None);
        Assert.Equal("hello", Assert.Single(set.Items).Text);
        var all = string.Concat(log.Lines);
        Assert.Contains("InvalidOperationException", all);
        Assert.DoesNotContain("secret", all);
        Assert.DoesNotContain("hello", all);
    }

    [Fact]
    public async Task Logs_contain_no_names_urls_paths_or_text()
    {
        var log = new CaptureLogger();
        File.WriteAllText(Path.Combine(_dir, "hello.txt"), "hello text");
        _src.Roots = [TempRoot()];
        _src.Returns("A", 1, Bytes("hello text"))
            .Returns("A", 2, new AiAttachmentRead.Failed("COMException"))
            .Returns("A", 3, new AiAttachmentRead.Cloud(AttFx.Share + "hello.txt", "Shared Secret"))
            .Returns("A", 4, new AiAttachmentRead.Cloud(null, "Shared Nothing"));
        await Make(log).CollectAsync(AttFx.Req(
            anchor: [AttFx.A(1, "secret-one.txt"), AttFx.A(2, "secret-two.txt"), AttFx.A(3, "c.docx"), AttFx.A(4, "d.docx")]),
            CancellationToken.None);

        var all = string.Concat(log.Lines);
        foreach (var s in new[] { "secret", "hello", "contoso", "sharepoint", "Shared", _dir })
            Assert.DoesNotContain(s, all);
    }

    [Theory]
    [InlineData(AttachmentReasons.NotSynced, 1, "NotSynced")]
    [InlineData(AttachmentReasons.FromOutside, 1, "FromOutside")]
    [InlineData(AttachmentReasons.EarlierEmailGone, 2, "EarlierEmailGone")]
    [InlineData(AttachmentReasons.PasswordProtected, 1, "PasswordProtected")]
    [InlineData(AttachmentReasons.TooLarge, 2, "TooLarge")]
    [InlineData(AttachmentReasons.NoText, 1, "NoText")]
    [InlineData("Clippy can't read .doc files", 1, "Format")]
    [InlineData(AttachmentReasons.Damaged, 1, "Damaged")]
    [InlineData(AttachmentReasons.TookTooLong, 1, "TookTooLong")]
    [InlineData(AttachmentReasons.OutOfTime, 2, "TookTooLong")]
    [InlineData(AttachmentReasons.TooMuchText, 1, "TooMuch")]
    [InlineData(AttachmentReasons.TooManyFiles, 1, "TooMuch")]
    [InlineData(AttachmentReasons.Image, 1, null)]
    [InlineData(AttachmentReasons.UnknownKind, 1, null)]
    [InlineData("Clippy can't read .xyz files", 1, null)]
    [InlineData(AttachmentReasons.NotMicrosoftLink, 1, null)]
    [InlineData(AttachmentReasons.NotOnEmail, 1, null)]
    [InlineData(AttachmentReasons.OutlookNotRunning, 1, null)]
    [InlineData(AttachmentReasons.TooLarge, 3, null)]
    public void NoteFor_table(string reason, int tier, string? expected)
    {
        var item = new PromptAttachment("f.doc", AttachmentOrigin.Email, AttachmentKind.Link, ExtractStatus.Skipped, "",
            reason, tier, null, null, AttFx.T0);
        var note = DraftAttachments.NoteFor(item);
        if (expected is null) Assert.Null(note);
        else
        {
            Assert.NotNull(note);
            Assert.Equal(Enum.Parse<AttachmentNoteKind>(expected), note.Kind);
            Assert.True(note.Shared);
            Assert.Equal(expected == "Format" ? "doc" : null, note.Extension);
        }
    }

    [Fact]
    public void NoteFor_read_and_truncated()
    {
        PromptAttachment Item(ExtractStatus s, int tier) =>
            new("f.txt", AttachmentOrigin.Email, AttachmentKind.File, s, "t", null, tier, null, null, AttFx.T0);
        Assert.Null(DraftAttachments.NoteFor(Item(ExtractStatus.Read, 1)));
        Assert.Null(DraftAttachments.NoteFor(Item(ExtractStatus.Truncated, 3)));
        var n = DraftAttachments.NoteFor(Item(ExtractStatus.Truncated, 2));
        Assert.Equal(new AttachmentNote(AttachmentNoteKind.Truncated, "f.txt", false), n);
    }

    [Fact]
    public async Task Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => new DraftAttachments(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Make().CollectAsync(null!, CancellationToken.None));
    }
}
