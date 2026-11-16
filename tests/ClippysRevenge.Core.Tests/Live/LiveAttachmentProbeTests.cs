using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using Microsoft.Win32;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// SPEC 26.3, 26.9 and 26.12 "Live": the Outlook questions behind attachment reading, answered against the real classic
/// Outlook and printed (not asserted, except where the product promises something). No Claude, nothing is sent. The
/// synthetic items are the test's own unsent drafts. Output holds counts and fixed words only: no URL, path, tenant or
/// file name of the user's machine.
/// </summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("os", "windows")]
public sealed partial class LiveAttachmentProbeTests(LiveOutlook fx)
{
    [Fact(Timeout = 180_000)]
    public async Task Attached_email_is_read_through_OpenSharedItem_without_ui()
    {
        var ct = TestContext.Current.CancellationToken;
        await fx.RequireReadyAsync(ct);
        var me = await fx.MeAsync();
        var ownStore = await fx.RunAsync("reading the store id", app => fx.StoreIdOf(app, ""));

        await fx.WithCleanupAsync([""], Outlook.OlDefaultFolders.olFolderDrafts, async scope =>
        {
            var itemFact = LiveAttachmentContent.NewFact("MAGNOLIA");
            var innerSubject = $"[{fx.RunToken}] attached inner";
            var outerSubject = $"[{fx.RunToken}] attached email";
            var inner = await CreateDraftAsync(me, innerSubject, "Inner message. Parking spot: " + itemFact + "\n" + fx.RunToken, ct);
            scope.Track(new TrackedItem(TrackedKind.Draft, "", inner.Id, ownStore, innerSubject));
            var outer = await CreateDraftAsync(me, outerSubject, "Synthetic message with an attached email.\n" + fx.RunToken, ct);
            scope.Track(new TrackedItem(TrackedKind.Draft, "", outer.Id, ownStore, outerSubject));

            var inspectorsBefore = await fx.RunAsync("counting windows", LiveAttachmentContent.InspectorCount);
            int type;
            try
            {
                type = await fx.RunAsync("attaching an email", app => LiveAttachmentContent.AttachItem(app, outer.Id, inner.Id, ownStore));
            }
            catch (COMException ex)
            {
                fx.Result("ai-attached-email",
                    $"skipped: attaching an item failed (HResult 0x{ex.HResult.ToString("x8", CultureInfo.InvariantCulture)})");
                Assert.Skip("Outlook refused to attach an email to the synthetic draft, so the attached-email test can't run here. Nothing was sent.");
                return;
            }

            var mailbox = new OutlookAiMailbox(fx.Worker, fx.Mail, fx.Stores);
            var identity = await mailbox.GetIdentityAsync(WorkPriority.Interactive, ct);
            var log = new LiveCapturingLogger();
            var aiFiles = LiveAttachmentContent.NewAiFiles(fx, log);
            using var watch = new AiFolderWatch(aiFiles.Folder);
            var source = new OutlookAttachmentSource(fx.Worker, fx.Mail, fx.Stores, aiFiles, new OneDriveRegistry(log));
            var collect = new DraftAttachments(source, TimeProvider.System, log);

            var live = await mailbox.ReadEmailAsync(outer.Id, ct);
            Assert.True(live is not null, "The synthetic email with an attached email can't be read back.");
            var attachments = live.Email.Attachments ?? [];
            var nameClass = attachments.Any(a => a.FileName.EndsWith(".msg", StringComparison.OrdinalIgnoreCase)) ? "msg" : "none";

            var clock = Stopwatch.StartNew();
            var set = await collect.CollectAsync(new AttachmentRequest("", outer.Id, live.Email.Subject, live.Email.Body,
                live.Email.SenderName, identity.OwnSmtp, live.Email.EventTime, attachments, [], identity, null,
                WorkPriority.Interactive, TimeZoneInfo.Local), ct);
            clock.Stop();

            Assert.True(nameClass == "msg", "The attached email has a name that doesn't end in .msg (SPEC 26.1).");
            Assert.True(set.Items.Count == 1, "The attached email was not found as exactly one attachment item.");
            var item = set.Items[0];
            Assert.True(item.Kind == AttachmentKind.Email && item.Status == ExtractStatus.Read,
                "The attached email was not read; see the printed name class (SPEC 26.1: attached emails are always read).");
            Assert.True(item.Text.Contains(itemFact, StringComparison.Ordinal) && item.Text.Contains(innerSubject, StringComparison.Ordinal),
                "The attached email's text is missing its body fact or subject.");
            var inspectorsAfter = await fx.RunAsync("counting windows", LiveAttachmentContent.InspectorCount);
            Assert.True(inspectorsAfter == inspectorsBefore, "OpenSharedItem opened a window.");

            Assert.True(LiveAttachmentContent.CountEntries(aiFiles.Folder) == 0, "ai-files is not empty after the read.");
            Assert.True(watch.Names.All(AiFilePaths.IsTempFileName), "A temp file in ai-files has an unexpected name.");
            fx.Result("ai-attached-email-temp",
                $"msg temp files seen {watch.Names.Count(n => n.EndsWith(".msg", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture)}");

            var outerAfter = await fx.RunAsync("reading the outer draft again", app => LiveDrafts.ReadDraft(fx, app, "", outer.Id));
            Assert.True(!outerAfter.Sent && outerAfter.AttachmentNames.Count == 1, "The outer draft changed.");

            fx.Result("ai-attached-email",
                $"type {type.ToString(CultureInfo.InvariantCulture)}, name {nameClass}, read in {clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms, inspectors unchanged, ai-files empty");
        });
    }

    [Fact(Timeout = 180_000)]
    public async Task Cloud_attachment_properties_are_recorded()
    {
        var ct = TestContext.Current.CancellationToken;
        await fx.RequireReadyAsync(ct);
        var me = await fx.MeAsync();
        var ownStore = await fx.RunAsync("reading the store id", app => fx.StoreIdOf(app, ""));

        await fx.WithCleanupAsync([""], Outlook.OlDefaultFolders.olFolderDrafts, async scope =>
        {
            var mailbox = new OutlookAiMailbox(fx.Worker, fx.Mail, fx.Stores);
            var log = new LiveCapturingLogger();
            var aiFiles = LiveAttachmentContent.NewAiFiles(fx, log);
            var registry = new OneDriveRegistry(log);
            using var watch = new AiFolderWatch(aiFiles.Folder);
            var source = new OutlookAttachmentSource(fx.Worker, fx.Mail, fx.Stores, aiFiles, registry);

            // 1. Synthetic: a by-reference attachment on the test's own draft.
            var subject = $"[{fx.RunToken}] cloud probe";
            var draft = await CreateDraftAsync(me, subject, "Synthetic message with a by-reference attachment.\n" + fx.RunToken, ct);
            scope.Track(new TrackedItem(TrackedKind.Draft, "", draft.Id, ownStore, subject));
            var (added, hresult) = await fx.RunAsync("adding a by-reference attachment", app =>
                LiveAttachmentContent.TryAddByReference(app, draft.Id, ownStore, LiveAttachmentContent.SyntheticCloudUrl, "clippy-live cloud.docx"));
            if (!added)
                fx.Result("ai-cloud-synthetic", $"olByReference add refused (HResult 0x{hresult.ToString("x8", CultureInfo.InvariantCulture)})");
            else
                await DescribeAsync("ai-cloud-synthetic", draft.Id, ownStore, mailbox, source, aiFiles, watch, registry, real: false, ct);

            // 2. Real, optional: one email the user prepared and named. Read-only, never tracked or deleted.
            var named = Environment.GetEnvironmentVariable(LiveAttachmentContent.CloudSubjectVariable)?.Trim();
            if (string.IsNullOrEmpty(named))
            {
                fx.Result("ai-cloud-real", "skipped: " + LiveAttachmentContent.CloudSubjectVariable + " not set");
                return;
            }
            if (!named.StartsWith("clippy-live", StringComparison.OrdinalIgnoreCase))
            {
                fx.Result("ai-cloud-real", "skipped: " + LiveAttachmentContent.CloudSubjectVariable + " must start with clippy-live");
                return;
            }
            var entryId = await fx.RunAsync("finding the prepared email", app => LiveAttachmentContent.FindInboxBySubject(fx, app, named));
            if (entryId is null)
            {
                fx.Result("ai-cloud-real", "not found");
                return;
            }
            await DescribeAsync("ai-cloud-real", entryId, ownStore, mailbox, source, aiFiles, watch, registry, real: true, ct);
        });
    }

    [Fact(Timeout = 60_000)]
    public async Task OneDrive_registry_layout_is_recorded()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);

        var registry = new OneDriveRegistry();
        var scopes = registry.ReadScopes();
        var personal = registry.ReadPersonal();
        var roots = registry.Read();

        static string Host(OneDriveScopeValues s) => LiveAttachmentContent.HostClass(s.UrlNamespace);
        var hosts = scopes.Select(Host).ToList();
        var other = hosts.Count(h => h is not ("sharepoint-my" or "sharepoint" or "d.docs.live.net"));

        string consumerKey = "absent", userFolder = "absent", cid = "absent";
        using (var key = Registry.CurrentUser.OpenSubKey(OneDriveRegistry.PersonalKey))
        {
            if (key is not null)
            {
                consumerKey = "present";
                var folder = key.GetValue("UserFolder")?.ToString();
                userFolder = string.IsNullOrWhiteSpace(folder) ? "absent" : Directory.Exists(folder.Trim()) ? "exists" : "missing";
                var id = key.GetValue("cid")?.ToString();
                cid = id is null ? "absent" : CidPattern().IsMatch(id) ? "valid" : "invalid";
            }
        }
        var consumerRoot = roots.Any(r => r.UrlPrefix.StartsWith("https://d.docs.live.net/", StringComparison.OrdinalIgnoreCase)
            && r.LibraryType == "personal");
        _ = personal;

        static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        int Lib(string t) => scopes.Count(s => string.Equals(s.LibraryType?.Trim(), t, StringComparison.OrdinalIgnoreCase));
        var known = Lib("personal") + Lib("mysite") + Lib("teamsite");
        var line = $"scopes {N(scopes.Count)} (personal {N(Lib("personal"))}, mysite {N(Lib("mysite"))}, teamsite {N(Lib("teamsite"))}, "
            + $"other {N(scopes.Count - known)}; folder scopes {N(scopes.Count(s => s.IsFolderScope?.Trim() == "1"))}; "
            + $"hosts sharepoint-my {N(hosts.Count(h => h == "sharepoint-my"))}, sharepoint {N(hosts.Count(h => h == "sharepoint"))}, "
            + $"d.docs.live.net {N(hosts.Count(h => h == "d.docs.live.net"))}, other {N(other)}), consumer key {consumerKey} "
            + $"(UserFolder {userFolder}, cid {cid}), usable roots {N(roots.Count)}, consumer fallback root {(consumerRoot ? "yes" : "no")}";

        foreach (var r in roots)
        {
            Assert.True(Uri.TryCreate(r.UrlPrefix, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && OneDriveLinks.IsMicrosoftFileHost(uri)
                && Path.IsPathFullyQualified(r.MountPoint) && Directory.Exists(r.MountPoint),
                "A usable OneDrive root breaks SPEC 26.3.");
        }
        fx.Result("onedrive-registry", line);
    }

    [GeneratedRegex(@"^[0-9A-Za-z]{1,32}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CidPattern();

    private async Task<CreateDraftResult> CreateDraftAsync(Recipient me, string subject, string body, CancellationToken ct) =>
        await new CreateDraftTool(fx.Settings,
                new Signatures(_ => null, Path.Combine(fx.TempRoot, "no-signatures")),
                new OutlookDraftWriter(fx.Worker, fx.Stores), fx.Hub)
            .RunAsync(new CreateDraftArgs(Recipients.Format([me]), subject, body), ct);

    /// <summary>Prints one line per attachment of the item and checks the two promises: a cloud attachment is reported
    /// as Cloud, and nothing is saved to ai-files for it.</summary>
    private async Task DescribeAsync(string key, string entryId, string ownStore, OutlookAiMailbox mailbox,
        OutlookAttachmentSource source, AiFiles aiFiles, AiFolderWatch watch, OneDriveRegistry registry, bool real,
        CancellationToken ct)
    {
        var probes = await fx.RunAsync("reading attachment properties", app => LiveAttachmentContent.Probe(app, entryId, ownStore));
        var live = await mailbox.ReadEmailAsync(entryId, ct);
        var wantedList = live?.Email.Attachments ?? [];
        var parts = new List<string>();
        foreach (var p in probes)
        {
            var cloud = AiAttachmentCom.IsCloud(p.Method, p.Type);
            var line = $"type {p.Type.ToString(CultureInfo.InvariantCulture)}, method {Num(p.Method)}, size {Num(p.Size)}, "
                + $"longfilename {LiveAttachmentContent.HostClass(p.LongFileName)}, longpathname {LiveAttachmentContent.HostClass(p.LongPathName)}";
            if (!real)
                line += ", url-equals-given " + (p.LongFileName is string a && a == LiveAttachmentContent.SyntheticCloudUrl
                    || p.LongPathName is string b && b == LiveAttachmentContent.SyntheticCloudUrl ? "yes" : "no");

            var url = AiAttachmentCom.CloudUrl(p.LongFileName, p.LongPathName);
            if (real)
            {
                var parses = url is not null && OneDriveLinks.TryParse(url, out _);
                var map = url is null ? "n/a" : OneDriveMap.Map(url, registry.Read()) switch
                {
                    MappedLink.Local l => File.Exists(l.Path) ? "local-exists" : "local-missing",
                    _ => "unmappable",
                };
                line += $", tryparse {(parses ? "yes" : "no")}, map {map}";
            }

            var wanted = wantedList.FirstOrDefault(w => w.Position == p.Position);
            var before = watch.Names.Count;
            string resultName;
            AiAttachmentRead? read = null;
            if (wanted is null)
                resultName = "not-listed";
            else
            {
                read = await source.ReadAsync("", entryId, wanted, AttachmentText.MaxFileBytes, WorkPriority.Interactive, ct);
                resultName = read switch
                {
                    AiAttachmentRead.Cloud c => $"Cloud(url={LiveAttachmentContent.HostClass(c.Url)})",
                    AiAttachmentRead.Failed f => $"Failed({f.ErrorType})",
                    null => "null",
                    _ => read.GetType().Name,
                };
            }
            parts.Add(line + ", read " + resultName);

            if (cloud && wanted is not null)
            {
                Assert.True(read is AiAttachmentRead.Cloud, "A cloud attachment was not reported as Cloud.");
                Assert.True(watch.Names.Count == before && LiveAttachmentContent.CountEntries(aiFiles.Folder) == 0,
                    "A cloud attachment was saved to disk.");
            }
        }
        fx.Result(key, parts.Count == 0 ? "no attachments" : string.Join(" | ", parts));
    }

    private static string Num(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "absent";
}

/// <summary>Records the names of files created directly in a folder while it lives.</summary>
internal sealed class AiFolderWatch : IDisposable
{
    private readonly object _gate = new();
    private readonly List<string> _names = [];
    private readonly FileSystemWatcher _watcher;

    public AiFolderWatch(string folder)
    {
        _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
        _watcher.Created += (_, e) =>
        {
            lock (_gate) _names.Add(e.Name ?? "");
        };
        _watcher.EnableRaisingEvents = true;
    }

    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_gate) return [.. _names];
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}
