using ClippysRevenge.App;
using ClippysRevenge.Core;
using ModelContextProtocol;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>Attachment listing and saving through PathPolicy (SPEC §11.3, §11.4, §12). Files go only to TempRoot.</summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("os", "windows")]
public sealed class LiveAttachmentTests(LiveOutlook fx)
{
    private AttachmentExport Export() =>
        new(new OutlookAttachmentSaver(fx.Worker, fx.Mail, fx.Stores), fx.Indexes, () => fx.Settings, fx.Hub);

    /// <summary>Up to 200 synced rows that have a real (non-inline) attachment. Skips when there is none.</summary>
    private async Task<List<MailboxHit>> RowsWithAttachmentsAsync()
    {
        await fx.EnsureSyncedAsync();
        var rows = fx.Indexes.Search(new SearchFilter { HasAttachment = true, Limit = 200 }, "")
            .Where(h => h.Email.Attachments.Any(a => !a.IsInline)).ToList();
        if (rows.Count == 0) Assert.Skip(SkipTexts.NoData("synced email with a real attachment"));
        return rows;
    }

    /// <summary>The smallest known non-inline attachment, to keep the disk writes small.</summary>
    private async Task<(MailboxHit Hit, AttachmentInfo Attachment)> PickAsync()
    {
        var rows = await RowsWithAttachmentsAsync();
        return rows.SelectMany(h => h.Email.Attachments.Where(a => !a.IsInline).Select(a => (Hit: h, Attachment: a)))
            .OrderBy(x => x.Attachment.Size ?? 1_000_000)
            .First();
    }

    [Fact(Timeout = 180_000)]
    public async Task Indexed_attachments_have_names_only()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var rows = await RowsWithAttachmentsAsync();
        foreach (var h in rows)
            Assert.All(h.Email.Attachments, a =>
            {
                Assert.False(string.IsNullOrWhiteSpace(a.FileName));
                Assert.True(a.Position >= 1);
            });

        foreach (var h in rows.Take(3))
        {
            var id = h.Email.Id;
            var live = (await fx.RunAsync("listing attachments", app => fx.Mail.ReadById(app, id))).Email;
            var indexed = h.Email.Attachments.Where(a => !a.IsInline).Select(a => a.FileName).Order(StringComparer.Ordinal);
            var current = live.Attachments.Where(a => !a.IsInline).Select(a => a.FileName).Order(StringComparer.Ordinal);
            Assert.Equal(indexed, current);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Get_attachment_saves_inside_allowed_root()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var (hit, att) = await PickAsync();
        var export = Export();
        var ct = TestContext.Current.CancellationToken;

        var first = await export.GetAsync(hit.Email.Id, null, att.Position, fx.AllowedDir, ct);
        Assert.StartsWith(fx.AllowedDir, first.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(first.Path), "The saved file does not exist: " + first.Path);
        Assert.True(new FileInfo(first.Path).Length > 0, "The saved file is empty.");

        var second = await export.GetAsync(hit.Email.Id, null, att.Position, fx.AllowedDir, ct);
        Assert.NotEqual(first.Path, second.Path);
        Assert.StartsWith(fx.AllowedDir, second.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(second.Path));
    }

    [Fact(Timeout = 180_000)]
    public async Task Get_attachment_outside_roots_is_refused()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var (hit, att) = await PickAsync();
        var outside = Path.Combine(fx.TempRoot, "outside-attachment");   // own dir: LiveDraftTests seeds TempRoot\outside
        Directory.CreateDirectory(outside);   // so that only the policy can refuse it

        await Assert.ThrowsAnyAsync<McpException>(() =>
            Export().GetAsync(hit.Email.Id, null, att.Position, outside, TestContext.Current.CancellationToken));
        var entries = Directory.GetFileSystemEntries(outside);
        Assert.True(entries.Length == 0, string.Join("; ", entries));
    }

    [Fact(Timeout = 180_000)]
    public async Task Save_attachments_with_limit_one()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var (_, att) = await PickAsync();
        var result = await Export().SaveAsync(new SaveAttachmentsArgs
        {
            SaveDir = fx.AllowedDir,
            Search = new SearchArgs { HasAttachment = true, AttachmentName = att.FileName, Limit = 1 },
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Saved.Count + result.Failed.Count >= 1, "Nothing saved and nothing reported.");
        Assert.Single(result.Saved.Select(s => s.Id).Concat(result.Failed.Select(f => f.Id)).Distinct());
        Assert.All(result.Saved, s => Assert.StartsWith(fx.AllowedDir, s.Path, StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Failed, f => Assert.False(string.IsNullOrWhiteSpace(f.Error)));
    }
}
