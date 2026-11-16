using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using ModelContextProtocol;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// M17.4 (SPEC 24.7 Live): one attachment download link against the real classic Outlook. The test runs its own
/// in-process McpHttpHost on an ephemeral loopback port with its own bearer token and its own link folder under the
/// run's temp root, so it never touches the running Clippy or the real links folder. Nothing is sent. The URL, the
/// link token and the bearer token are never put in assertion messages or results.
/// </summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("os", "windows")]
public sealed class LiveAttachmentLinkTests(LiveOutlook fx)
{
    /// <summary>A space and a non-ASCII letter exercise filename/filename* and LinkFileName.ForShell.
    /// Content is random bytes, not a real PDF (Outlook doesn't check).</summary>
    private const string AttachmentName = "clippy-live Prüfbericht.pdf";

    /// <summary>Several FilesEndpoint copy chunks plus an odd tail.</summary>
    private const int AttachmentBytes = 300_007;

    [Fact(Timeout = 180_000)]
    public async Task Attachment_link_downloads_once_with_the_saved_bytes_and_leaves_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await fx.RequireReadyAsync(ct);
        var me = await fx.MeAsync();
        var storeId = await fx.RunAsync("reading the store id", app => fx.StoreIdOf(app, ""));
        var linksDir = Path.Combine(fx.TempRoot, "links");

        await fx.WithCleanupAsync([""], [Outlook.OlDefaultFolders.olFolderDrafts, Outlook.OlDefaultFolders.olFolderDeletedItems], async scope =>
        {
            // 1. Source file.
            var original = RandomNumberGenerator.GetBytes(AttachmentBytes);
            var source = Path.Combine(fx.AllowedDir, AttachmentName);
            await File.WriteAllBytesAsync(source, original, ct);

            // 2. Synthetic email: unsent, self-addressed draft with the attachment, no signature.
            var subject = $"[{fx.RunToken}] attachment link";
            var created = await new CreateDraftTool(fx.Settings,
                    new Signatures(_ => null, Path.Combine(fx.TempRoot, "no-signatures")),
                    new OutlookDraftWriter(fx.Worker, fx.Stores), fx.Hub)
                .RunAsync(new CreateDraftArgs(Recipients.Format([me]), subject,
                    "Synthetic message for the attachment link live test.\n" + fx.RunToken, Attachments: [source]), ct);
            scope.Track(new TrackedItem(TrackedKind.Draft, "", created.Id, storeId, subject));

            // 3. Reference copy by an independent SaveAsFile.
            var refDir = Path.Combine(fx.TempRoot, "link-reference");
            Directory.CreateDirectory(refDir);
            var refPath = Path.Combine(refDir, "reference.bin");
            var createdId = created.Id;
            await fx.RunAsync("saving the reference copy", app =>
            {
                SaveReference(app, createdId, storeId, AttachmentName, refPath);
                return true;
            });
            var reference = File.ReadAllBytes(refPath);
            Assert.True(reference.AsSpan().SequenceEqual(original), "Outlook's SaveAsFile copy differs from the attached file.");

            var bearer = AuthToken.Generate();
            var indexedBefore = fx.Indexes.For("").Status().TotalEmails;

            using (var hub = new ActivityHub())
            using (var links = new AttachmentLinks(new AttachmentLinksOptions
            {
                Folder = linksDir,
                EnsurePrivateFolder = PrivateFolder.Ensure,
            }))
            {
                await using var host = new McpHttpHost(new McpHostOptions
                {
                    Tokens = new TokenHolder(bearer),
                    Hub = hub,
                    Links = links,
                });
                Assert.True(await host.StartAsync(0, ct), "The in-process MCP host did not start on a loopback port.");
                var port = host.Port!.Value;
                var tool = new AttachmentLinkTool(links, new OutlookAttachmentLinkSaver(fx.Worker, fx.Mail, fx.Stores),
                    () => host.Port ?? throw new InvalidOperationException("The test host stopped."), TimeZoneInfo.Local);
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });

                // 4. A bad selection leaves nothing behind.
                var bad = await Assert.ThrowsAnyAsync<McpException>(
                    () => tool.CreateAsync(created.Id, "clippy-live-missing.pdf", null, ct));
                Assert.True(bad.Message.StartsWith(
                        "This email has no attachment named \"clippy-live-missing.pdf\". Its attachments: ", StringComparison.Ordinal),
                    "The bad selection did not give the attachment-selection message.");
                Assert.False(bad is AttachmentLinkException, "A bad selection must not be a link refusal.");
                Assert.True(links.OpenCount == 0, "A bad selection left an open link.");
                Assert.True(CountEntries(linksDir) == 0, "A bad selection left a file in the link folder.");

                // 5. Make the link.
                var before = DateTimeOffset.Now;
                var r = await tool.CreateAsync(created.Id, AttachmentName, null, ct);
                var after = DateTimeOffset.Now;
                Assert.True(r.FileName == AttachmentName, "The link names a different file.");
                Assert.True(r.Size == AttachmentBytes, "The link reports a different size.");
                Assert.True(Regex.IsMatch(r.Url, $"^http://127\\.0\\.0\\.1:{port}/files/[A-Za-z0-9_-]{{43}}$"),
                    "The link url has an unexpected shape.");
                Assert.True(r.Curl == LinkTexts.Curl(LinkFileName.ForShell(AttachmentName), r.Url),
                    "The curl command is not the pinned one.");
                Assert.True(r.Curl.Contains("$CLIPPY_TOKEN", StringComparison.Ordinal), "The curl command lacks $CLIPPY_TOKEN.");
                Assert.False(r.Curl.Contains(bearer, StringComparison.Ordinal), "The curl command contains the bearer token.");
                Assert.True(r.Note == LinkTexts.Note, "The note is not the pinned one.");
                var expires = DateTimeOffset.ParseExact(r.ExpiresAt, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                Assert.True(before + AttachmentLinks.LinkLifetime - TimeSpan.FromSeconds(1) <= expires
                            && expires <= after + AttachmentLinks.LinkLifetime + TimeSpan.FromSeconds(1),
                    "expires_at is not about ten minutes from now.");
                Assert.True(links.OpenCount == 1, "Expected exactly one open link.");

                // 6. Private folder while the link is open.
                Assert.StartsWith(fx.TempRoot, linksDir, StringComparison.OrdinalIgnoreCase);
                PrivateFolderTests.AssertUserOnly(linksDir);
                Assert.Null(new DirectoryInfo(linksDir).LinkTarget);
                var entries = Directory.EnumerateFileSystemEntries(linksDir).ToList();
                Assert.True(entries.Count == 1, "Expected exactly one file in the link folder.");
                var f = entries[0];
                Assert.True(File.Exists(f), "The link folder entry is not a file.");
                Assert.True(LinkPaths.IsTempFileName(Path.GetFileName(f)), "The temp file name is not <32 hex>.bin.");
                Assert.True(new FileInfo(f).Length == AttachmentBytes, "The temp file has a different length.");
                PrivateFolderTests.AssertOnlyMe(new FileInfo(f).GetAccessControl(AccessControlSections.Access), directory: false);

                // 7. Bearer required; a 401 doesn't use the link up.
                using (var none = await http.SendAsync(Get(r.Url, null), HttpCompletionOption.ResponseContentRead, ct))
                {
                    Assert.True(none.StatusCode == HttpStatusCode.Unauthorized, "A GET without a bearer token was not 401.");
                    Assert.True((await none.Content.ReadAsByteArrayAsync(ct)).Length == 0, "The 401 body was not empty.");
                }
                using (var wrong = await http.SendAsync(Get(r.Url, AuthToken.Generate()), HttpCompletionOption.ResponseContentRead, ct))
                {
                    Assert.True(wrong.StatusCode == HttpStatusCode.Unauthorized, "A GET with a wrong bearer token was not 401.");
                    Assert.True((await wrong.Content.ReadAsByteArrayAsync(ct)).Length == 0, "The 401 body was not empty.");
                }
                Assert.True(links.OpenCount == 1, "A 401 used the link up.");

                // 8. First GET with the bearer token.
                using (var ok = await http.SendAsync(Get(r.Url, bearer), HttpCompletionOption.ResponseContentRead, ct))
                {
                    Assert.True(ok.StatusCode == HttpStatusCode.OK, "The first authorized GET was not 200.");
                    var body = await ok.Content.ReadAsByteArrayAsync(ct);
                    Assert.True(body.Length == reference.Length && body.AsSpan().SequenceEqual(reference),
                        "The downloaded bytes differ from Outlook's SaveAsFile copy.");
                    foreach (var (name, value) in FileDownloadHeaders.Build(AttachmentName, AttachmentBytes))
                        Assert.True(Header(ok, name) == value, "The response header " + name + " is not the pinned one.");
                }

                // 9. Gone afterwards (before anything is disposed).
                Assert.True(await EventuallyAsync(() => CountEntries(linksDir) == 0, ct),
                    "The link's temp file was not deleted after the download.");
                Assert.True(await EventuallyAsync(() => hub.GetEvents().Count(e =>
                        e.Kind == ActivityKind.Tool && e.Summary == FilesEndpoint.DownloadEvent) == 1, ct),
                    "Expected exactly one attachment download event.");
                Assert.False(hub.GetEvents().Any(e => e.Summary == FilesEndpoint.DownloadFailedEvent),
                    "A failed attachment download event was recorded.");
                Assert.True(links.OpenCount == 0, "The link is still open after the download.");

                // 10. Second GET is 404.
                using (var again = await http.SendAsync(Get(r.Url, bearer), HttpCompletionOption.ResponseContentRead, ct))
                {
                    Assert.True(again.StatusCode == HttpStatusCode.NotFound, "The second GET was not 404.");
                    Assert.True((await again.Content.ReadAsByteArrayAsync(ct)).Length == 0, "The 404 body was not empty.");
                }
                Assert.True(CountEntries(linksDir) == 0, "The link folder is not empty after the second GET.");

                // 11. Nothing indexed.
                Assert.True(fx.Indexes.For("").Status().TotalEmails == indexedBefore, "The link path changed the index.");

                // 12. ACL still user-only.
                PrivateFolderTests.AssertUserOnly(linksDir);
            }

            // Client, host, link store and hub are disposed now; still nothing in the folder.
            Assert.True(CountEntries(linksDir) == 0, "The link folder is not empty after the store was disposed.");

            // 13.
            fx.Result("m17-link",
                $"{AttachmentBytes} bytes downloaded once and equal to SaveAsFile; second GET 404; 401 without bearer kept the link; folder empty and user-only");
        });
    }

    private static int CountEntries(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).Count() : 0;

    private static HttpRequestMessage Get(string url, string? bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        return req;
    }

    private static string Header(HttpResponseMessage r, string name)
    {
        if (r.Headers.NonValidated.TryGetValues(name, out HeaderStringValues v)
            || r.Content.Headers.NonValidated.TryGetValues(name, out v))
            return string.Join(",", v);
        return "";
    }

    /// <summary>Worker thread. Opens the item by (entryId, storeId), finds the one attachment whose FileName equals
    /// name (ordinal) and saves it to path. Releases everything.</summary>
    private static void SaveReference(Outlook.Application app, string entryId, string storeId, string name, string path)
    {
        Outlook.NameSpace? session = null;
        object? item = null;
        Outlook.Attachments? atts = null;
        try
        {
            session = app.Session;
            item = session.GetItemFromID(entryId, storeId);
            atts = ((Outlook.MailItem)item).Attachments;
            var count = atts.Count;
            for (var i = 1; i <= count; i++)
            {
                Outlook.Attachment? a = null;
                try
                {
                    a = atts[i];
                    if (string.Equals(a.FileName, name, StringComparison.Ordinal))
                    {
                        a.SaveAsFile(path);
                        return;
                    }
                }
                finally
                {
                    LiveDrafts.Rel(a);
                }
            }
            throw new InvalidOperationException("The test attachment is not on the synthetic email.");
        }
        finally
        {
            LiveDrafts.Rel(atts);
            LiveDrafts.Rel(item);
            LiveDrafts.Rel(session);
        }
    }

    /// <summary>Polls every 100 ms for up to 5 s until cond() is true; returns the last cond() value.</summary>
    private static async Task<bool> EventuallyAsync(Func<bool> cond, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var ok = cond();
        while (!ok && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct);
            ok = cond();
        }
        return ok;
    }
}
