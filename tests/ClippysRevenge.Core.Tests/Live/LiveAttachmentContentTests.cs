using System.Diagnostics;
using System.Globalization;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// SPEC 26.12 "Live": attachment content and synced OneDrive files in manual AI drafts, against the real classic
/// Outlook and the real Claude CLI (Haiku, effort low, one call per test). Sources are synthetic, self-addressed,
/// unsent drafts; nothing is sent. The Claude tests need CLIPPY_LIVE=1 and CLIPPY_LIVE_CLAUDE=1; the synced-OneDrive
/// test also needs CLIPPY_LIVE_ONEDRIVE=1 because it writes two files into the user's own OneDrive and deletes them.
/// Nothing here prints mail text, prompts, model output, URLs, local paths or file names of the user's machine.
/// </summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("live", "claude")]
[Trait("os", "windows")]
public sealed class LiveAttachmentContentTests(LiveOutlook fx)
{
    private const string NotSyncedSuffix = " (not synced to this PC).";

    [Fact(Timeout = 300_000)]
    public async Task Manual_draft_quotes_pdf_and_docx_facts_and_notes_an_unsynced_link()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = LiveClaude.RequireEnabled();
        await fx.RequireReadyAsync(ct);
        var me = await fx.MeAsync();
        var ownStore = await fx.RunAsync("reading the store id", app => fx.StoreIdOf(app, ""));
        var startUtc = DateTimeOffset.UtcNow;

        await fx.WithCleanupAsync([""], Outlook.OlDefaultFolders.olFolderDrafts, async scope =>
        {
            var pdfFact = LiveAttachmentContent.NewFact("ORCHID");
            var docxFact = LiveAttachmentContent.NewFact("TANGERINE");
            var pdfPath = Path.Combine(fx.AllowedDir, LiveAttachmentContent.PdfName);
            var docxPath = Path.Combine(fx.AllowedDir, LiveAttachmentContent.DocxName);
            await File.WriteAllBytesAsync(pdfPath, LiveAttachmentContent.Pdf(pdfFact), ct);
            await File.WriteAllBytesAsync(docxPath, LiveAttachmentContent.Docx(docxFact), ct);

            var subject = $"[{fx.RunToken}] AI attachments";
            var body = string.Join("\n",
                "Hi,",
                "",
                "Please reply with the access code from the attached PDF and the meeting room from the attached Word document, quoted exactly.",
                "The draft notes are here: " + LiveAttachmentContent.UnsyncedLink,
                "",
                "Thanks,",
                "Sam",
                "",
                fx.RunToken);
            var src = await CreateSourceAsync(me, subject, body, [pdfPath, docxPath], ct);
            scope.Track(new TrackedItem(TrackedKind.Draft, "", src.Id, ownStore, subject));

            await BaselineOrSkipAsync(src.Id, ownStore);

            string? Check(ModelRequest r) =>
                !r.SystemPrompt.Contains(DraftPrompt.AttachmentsNote, StringComparison.Ordinal) ? "attachments-note"
                : !r.UserPrompt.Contains("<attachments>\n", StringComparison.Ordinal) ? "attachments-block"
                : !r.UserPrompt.Contains("<attachment name=\"Live facts.pdf\" source=\"email\" kind=\"file\" status=\"read\">", StringComparison.Ordinal)
                  || !r.UserPrompt.Contains("<attachment name=\"Live agenda.docx\" source=\"email\" kind=\"file\" status=\"read\">", StringComparison.Ordinal)
                    ? "attachment-status"
                : !r.UserPrompt.Contains(pdfFact, StringComparison.Ordinal) || !r.UserPrompt.Contains(docxFact, StringComparison.Ordinal)
                    ? "attachment-text"
                : !r.UserPrompt.Contains("source=\"email\" kind=\"link\" status=\"skipped\" reason=\"not synced to this PC\"/>", StringComparison.Ordinal)
                    ? "unsynced-link"
                : null;

            using var run = await AttachmentDraftRun.CreateAsync(fx, scope, model, ownStore, src.Id, Check, "ai-attach", ct);
            var (_, f, elapsed) = await run.DraftAsync(ct);

            // Facts and the unsynced-link note.
            Assert.True(f.Text.Contains(pdfFact, StringComparison.Ordinal), "The AI draft doesn't quote the fact from the PDF.");
            Assert.True(f.Text.Contains(docxFact, StringComparison.Ordinal), "The AI draft doesn't quote the fact from the Word document.");
            var plan = run.Writer.Plans[0];
            var plain = DraftHtml.ToPlainText(plan.HtmlFragment);
            var lines = plain.Split('\n').Select(l => l.Trim()).ToList();
            var noteIndex = lines.FindIndex(l => l.StartsWith("Clippy: Couldn't read the shared file ", StringComparison.Ordinal)
                && l.EndsWith(NotSyncedSuffix, StringComparison.Ordinal));
            Assert.True(noteIndex >= 0, "The reply has no note for the unsynced shared file.");
            Assert.True(f.Text.Contains(NotSyncedSuffix, StringComparison.Ordinal), "The saved reply doesn't show the unsynced-file note.");
            var fallback = lines[noteIndex] == "Clippy: " + AttachmentNoteTexts.Text(
                new AttachmentNote(AttachmentNoteKind.NotSynced, "a shared file on contoso.sharepoint.com", true));
            var noteAt = plain.IndexOf(lines[noteIndex], StringComparison.Ordinal);
            var factAt = plain.IndexOf(pdfFact, StringComparison.Ordinal);
            Assert.True(factAt >= 0 && noteAt < factAt, "The note is not above the model's text.");

            await run.AssertCommonAsync(f, subject, src.Id, startUtc, "attachments: 2 read, 1 not read",
                [pdfFact, docxFact, LiveAttachmentContent.PdfName, LiveAttachmentContent.DocxName, "contoso", fx.AllowedDir,
                    run.AiFiles.Folder], [pdfFact, docxFact], expectedWatcherNames: null);

            var ok = (ModelResult.Ok)run.Guard.LastResult!;
            fx.Result("ai-attachments",
                $"drafted in {AiHtmlChecks.Seconds(elapsed)} s, model {model}, facts quoted 2/2, note={(fallback ? "fallback" : "text")}, "
                + $"sender={run.SenderClass}, temp files seen {run.WatcherNames.Count.ToString(CultureInfo.InvariantCulture)}, ai-files empty, "
                + AttachmentDraftRun.TokenText(ok));
        });
    }

    [Fact(Timeout = 300_000)]
    public async Task Manual_draft_uses_synced_onedrive_files_by_direct_and_r_links()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = LiveClaude.RequireEnabled();
        await fx.RequireReadyAsync(ct);

        // Opt-in: this test writes two files into the user's own OneDrive. Decide before anything is touched.
        if (Environment.GetEnvironmentVariable(LiveAttachmentContent.OneDriveVariable) != "1")
        {
            fx.Result("ai-onedrive", "skipped: " + LiveAttachmentContent.OneDriveVariable + " not set");
            Assert.Skip("Set CLIPPY_LIVE_ONEDRIVE=1 to run the synced-OneDrive test: it writes two small test files into your own business OneDrive and deletes them again (they stay in the OneDrive recycle bin). Nothing was sent.");
        }

        var roots = new OneDriveRegistry().Read();
        var root = LiveAttachmentContent.OwnBusinessRoot(roots);
        if (root is null)
        {
            fx.Result("ai-onedrive", "skipped: no business OneDrive synced");
            Assert.Skip("No business OneDrive (…-my.sharepoint.com) is synced to this PC, so the synced-file draft test can't run here. Nothing was sent.");
        }

        var me = await fx.MeAsync();
        var ownStore = await fx.RunAsync("reading the store id", app => fx.StoreIdOf(app, ""));
        var startUtc = DateTimeOffset.UtcNow;

        using var folder = new SyncedTestFolder(root, fx.RunToken);
        try
        {
            await fx.WithCleanupAsync([""], Outlook.OlDefaultFolders.olFolderDrafts, async scope =>
            {
                var noteFact = LiveAttachmentContent.NewFact("JUNIPER");
                var budgetFact = LiveAttachmentContent.NewFact("SAFFRON");
                var docxPath = folder.Write(LiveAttachmentContent.SyncedDocxName, LiveAttachmentContent.Docx(noteFact));
                var xlsxPath = folder.Write(LiveAttachmentContent.SyncedXlsxName, LiveAttachmentContent.Xlsx(budgetFact));
                var direct = LiveAttachmentContent.SyncedUrl(root, false, fx.RunToken, LiveAttachmentContent.SyncedDocxName);
                var rLink = LiveAttachmentContent.SyncedUrl(root, true, fx.RunToken, LiveAttachmentContent.SyncedXlsxName);

                var mappedDirect = OneDriveMap.Map(direct, roots);
                var mappedR = OneDriveMap.Map(rLink, roots);
                Assert.True(mappedDirect is MappedLink.Local d
                    && string.Equals(d.Path, docxPath, StringComparison.OrdinalIgnoreCase)
                    && mappedR is MappedLink.Local r && string.Equals(r.Path, xlsxPath, StringComparison.OrdinalIgnoreCase),
                    "The synced link doesn't map to the file the test wrote.");

                var subject = $"[{fx.RunToken}] AI synced files";
                var body = string.Join("\n",
                    "Hi,",
                    "",
                    "Please reply with the meeting room from the shared notes and the budget code from the shared spreadsheet, quoted exactly.",
                    "Notes: " + direct,
                    "Budget: " + rLink,
                    "",
                    "Thanks,",
                    "Sam",
                    "",
                    fx.RunToken);
                var src = await CreateSourceAsync(me, subject, body, null, ct);
                scope.Track(new TrackedItem(TrackedKind.Draft, "", src.Id, ownStore, subject));

                await BaselineOrSkipAsync(src.Id, ownStore);

                string? Check(ModelRequest r2) =>
                    !r2.SystemPrompt.Contains(DraftPrompt.AttachmentsNote, StringComparison.Ordinal) ? "attachments-note"
                    : !r2.UserPrompt.Contains("<attachment name=\"Live notes.docx\" source=\"email\" kind=\"link\" status=\"read\">", StringComparison.Ordinal)
                      || !r2.UserPrompt.Contains("<attachment name=\"Live budget.xlsx\" source=\"email\" kind=\"link\" status=\"read\">", StringComparison.Ordinal)
                        ? "attachment-status"
                    : !r2.UserPrompt.Contains(noteFact, StringComparison.Ordinal) || !r2.UserPrompt.Contains(budgetFact, StringComparison.Ordinal)
                        ? "attachment-text"
                    : null;

                using var run = await AttachmentDraftRun.CreateAsync(fx, scope, model, ownStore, src.Id, Check, "ai-attach-od", ct);
                var (_, f, elapsed) = await run.DraftAsync(ct);

                Assert.True(f.Text.Contains(noteFact, StringComparison.Ordinal), "The AI draft doesn't quote the fact from the shared notes.");
                Assert.True(f.Text.Contains(budgetFact, StringComparison.Ordinal), "The AI draft doesn't quote the fact from the shared spreadsheet.");
                var plain = DraftHtml.ToPlainText(run.Writer.Plans[0].HtmlFragment);
                Assert.True(!plain.Split('\n').Any(l => l.Trim().StartsWith("Clippy: Couldn't read", StringComparison.Ordinal)),
                    "The reply has a note about a file that could not be read.");

                await run.AssertCommonAsync(f, subject, src.Id, startUtc, "attachments: 2 read, 0 not read",
                    [noteFact, budgetFact, folder.Folder, root.MountPoint, LiveAttachmentContent.SyncedDocxName,
                        LiveAttachmentContent.SyncedXlsxName, new Uri(direct).Host, run.AiFiles.Folder],
                    [noteFact, budgetFact], expectedWatcherNames: 0);

                var ok = (ModelResult.Ok)run.Guard.LastResult!;
                fx.Result("ai-onedrive",
                    $"drafted in {AiHtmlChecks.Seconds(elapsed)} s, direct and /:x:/r/ links read, facts quoted 2/2, sender={run.SenderClass}, "
                    + $"library={root.LibraryType}, files removed, " + AttachmentDraftRun.TokenText(ok));
            });
        }
        finally
        {
            folder.Dispose();
            foreach (var marker in folder.Leftovers)
                fx.Leftover($"OneDrive|{fx.RunToken}|{marker}");
        }

        Assert.True(folder.Leftovers.Count == 0, "The synced test files or folder could not be removed.");
    }

    private async Task<DraftSource> CreateSourceAsync(Recipient me, string subject, string body, string[]? attachments, CancellationToken ct)
    {
        var made = await new CreateDraftTool(fx.Settings,
                new Signatures(_ => null, Path.Combine(fx.TempRoot, "no-signatures")),
                new OutlookDraftWriter(fx.Worker, fx.Stores), fx.Hub)
            .RunAsync(new CreateDraftArgs(Recipients.Format([me]), subject, body, Attachments: attachments), ct);
        return new DraftSource(made.Id);
    }

    private async Task BaselineOrSkipAsync(string srcId, string ownStore)
    {
        var (html, hresult) = await fx.RunAsync("checking Outlook's own reply", app => LiveDrafts.TryOwnReply(app, srcId, ownStore));
        if (html is null)
        {
            fx.Result("ai-attach-baseline",
                $"skipped: reply to an unsent item failed (HResult 0x{hresult.ToString("x8", CultureInfo.InvariantCulture)})");
            Assert.Skip("Outlook can't reply to an unsent test item on this profile, so the attachment draft test can't run here. Nothing was sent.");
        }
    }

    private sealed record DraftSource(string Id);
}

/// <summary>The shared wiring of A (SPEC 26.12): production attachment pipeline, usage recording and guard model.</summary>
internal sealed class AttachmentDraftRun : IDisposable
{
    private readonly LiveOutlook _fx;
    private readonly string _srcId;
    private readonly object _namesLock = new();
    private readonly List<string> _names = [];
    private readonly FileSystemWatcher _watcher;
    private readonly ActivityHub _hub;
    private readonly DraftWorker _drafts;
    private readonly SenderFillingMailbox _filling;
    private readonly AiUsage _usage;
    private RepliedDraft? _made;

    private AttachmentDraftRun(LiveOutlook fx, LiveOutlook.Scope scope, string model, string ownStore, string srcId,
        Func<ModelRequest, string?> extraCheck, string workName, AiIdentity identity)
    {
        _fx = fx;
        _srcId = srcId;

        // A.1 Settings.
        var ai = LiveClaude.Settings(model, DateTimeOffset.UtcNow);
        ai.MeetingTimes = false;
        ai.ReadAttachments = true;
        var s = fx.Settings.Clone();
        s.AiDrafting = ai;

        // A.2 The production attachment pipeline over the run's own ai-files folder.
        Log = new LiveCapturingLogger();
        AiFiles = LiveAttachmentContent.NewAiFiles(fx, Log);
        var registry = new OneDriveRegistry(Log);
        var source = new OutlookAttachmentSource(fx.Worker, fx.Mail, fx.Stores, AiFiles, registry);
        var collect = new DraftAttachments(source, TimeProvider.System, Log);
        _watcher = new FileSystemWatcher(AiFiles.Folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
        _watcher.Created += (_, e) =>
        {
            lock (_namesLock) _names.Add(e.Name ?? "");
        };
        _watcher.EnableRaisingEvents = true;

        // A.3 Model with usage recording.
        var cli = LiveClaude.CreateModel(() => s.AiDrafting, Path.Combine(fx.TempRoot, workName));
        Cli = cli;
        Guard = new GuardedModel(cli, model, fx.RunToken, meetingNote: false, extraCheck: extraCheck);
        _usage = new AiUsage(() => fx.Indexes.For(""), TimeZoneInfo.Local);
        _usage.Load();
        var recording = new UsageRecordingModel(Guard, () => s.AiDrafting, _usage, Log);

        // A.4 Mailbox with the sender filled in only when Outlook gave no SMTP address.
        _filling = new SenderFillingMailbox(new OutlookAiMailbox(fx.Worker, fx.Mail, fx.Stores), srcId, identity.OwnSmtp);
        AiMailbox = new FixedSelectionMailbox(_filling, new SelectedEmail(srcId, ownStore));

        // A.5 Worker.
        Writer = new RecordingReplyWriter(new OutlookReplyDraftWriter(fx.Worker, fx.Mail, fx.Stores), srcId, m =>
        {
            _made = m;
            scope.Track(new TrackedItem(TrackedKind.Draft, "", m.EntryId, ownStore, $"[{fx.RunToken}] {m.Subject}"));
        });
        _hub = new ActivityHub();
        _drafts = new DraftWorker(new DraftWorkerServices(() => s, fx.Indexes, AiMailbox, Writer, recording,
            NoStyleGuide.Instance, () => null, _hub, new BalloonCenter(),
            () => fx.Worker.State == OutlookConnectionState.Available, () => false, TimeZoneInfo.Local,
            Logger: Log, Attachments: collect));
    }

    public LiveCapturingLogger Log { get; }
    public AiFiles AiFiles { get; }
    public ClaudeCliModel Cli { get; }
    public GuardedModel Guard { get; }
    public FixedSelectionMailbox AiMailbox { get; }
    public RecordingReplyWriter Writer { get; }
    public ActivityHub Hub => _hub;
    public IReadOnlyList<string> WatcherNames
    {
        get
        {
            lock (_namesLock) return [.. _names];
        }
    }

    /// <summary>"filled" when the sender SMTP was given by the test, else "outlook".</summary>
    public string SenderClass => _filling.Filled > 0 ? "filled" : "outlook";

    public static async Task<AttachmentDraftRun> CreateAsync(LiveOutlook fx, LiveOutlook.Scope scope, string model, string ownStore,
        string srcId, Func<ModelRequest, string?> extraCheck, string workName, CancellationToken ct)
    {
        var raw = new OutlookAiMailbox(fx.Worker, fx.Mail, fx.Stores);
        var identity = await raw.GetIdentityAsync(WorkPriority.Interactive, ct);
        var live = await raw.ReadEmailAsync(srcId, ct);
        Assert.True(live is not null, "The synthetic email can't be read back.");
        var sender = MailAddresses.LooksLikeSmtp(live.Email.SenderEmail) ? live.Email.SenderEmail : identity.OwnSmtp;
        Assert.True(DraftAttachmentPlan.MayOpenShared(sender, identity, ""),
            "The synthetic email's sender isn't the own address, so its links would be refused.");

        var run = new AttachmentDraftRun(fx, scope, model, ownStore, srcId, extraCheck, workName, identity);
        try
        {
            await LiveClaude.RequireReadyAsync(run.Cli, ct);
        }
        catch
        {
            run.Dispose();
            throw;
        }
        return run;
    }

    /// <summary>A.7: runs the production manual path, tags the reply at once and handles the outcome.</summary>
    public async Task<(ManualDraftResult Result, DraftFacts F, TimeSpan Elapsed)> DraftAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var result = await _drafts.DraftSelectedAsync(ct);
        watch.Stop();

        DraftFacts? f = null;
        if (Writer.Plans.Count == 1 && _made is not null)
        {
            var entryId = _made.EntryId;
            f = await _fx.RunAsync("reading the AI reply", app => LiveDrafts.ReadDraft(_fx, app, "", entryId, tag: true));
        }

        if (Guard.Tripped is not null)
            Assert.Fail("The live guard refused the Claude request (" + Guard.Tripped + "). Nothing was sent to Claude.");
        if (Guard.LastResult is ModelResult.RateLimited) Assert.Skip(LiveClaude.UsageLimit);
        const string notReadyPrefix = "Claude isn't ready: ";
        if (result.Message.StartsWith(notReadyPrefix, StringComparison.Ordinal))
            Assert.Skip(LiveClaude.NotReady(result.Message[notReadyPrefix.Length..]));
        Assert.True(result.Drafted, "Manual draft failed: " + result.Message);
        Assert.True(f is not null && _made is not null, "The AI reply was not read back.");
        return (result, f, watch.Elapsed);
    }

    /// <summary>The assertions of B.5 and C.6 that both tests share.</summary>
    /// <param name="expectedWatcherNames">null = any number (printed, not asserted); else exactly that many.</param>
    public async Task AssertCommonAsync(DraftFacts f, string subject, string srcId, DateTimeOffset startUtc, string eventSuffixText,
        string[] secrets, string[] indexSecrets, int? expectedWatcherNames)
    {
        Assert.True(Guard.Calls == 1, "Claude was not called exactly once.");
        Assert.True(Guard.LastResult is ModelResult.Ok, "The Claude call did not give a result.");
        Assert.True(AiMailbox.SelectionCalls == 1, "The fixed selection was not used exactly once.");

        Assert.True(Writer.Plans.Count == 1, "Exactly one reply plan was expected.");
        var plan = Writer.Plans[0];
        Assert.True(plan.Id == srcId && plan.Mailbox is null && plan.Marker == DraftMarker.Ai, "The reply plan is not the expected AI reply.");
        Assert.True(!plan.SignatureAdded, "The reply plan added a signature.");
        Assert.True(AiHtmlChecks.FragmentIsSanitized(plan.HtmlFragment), "The AI fragment has a tag outside the allow-list.");

        Assert.True(f.Subject == ReplySubject.Normalize(subject), "The reply subject is not the normalized source subject.");
        Assert.StartsWith("Re:: ", f.Subject, StringComparison.Ordinal);
        Assert.True(!f.Sent, "The reply draft is marked as sent.");
        Assert.True(f.ParentId == f.DraftsId, "The reply draft is not in the own Drafts folder.");
        Assert.True(f.ClippyDraft == DraftMarker.Ai, "The reply draft does not carry the ai marker.");

        var srcAfter = await _fx.RunAsync("reading the source again", app => LiveDrafts.ReadDraft(_fx, app, "", srcId));
        Assert.True(!srcAfter.Sent && srcAfter.Subject == subject, "The source item changed.");

        var decision = _fx.Indexes.For("").GetDecision(srcId);
        Assert.True(decision is not null, "No decision row was written.");
        Assert.True(decision.Outcome == AiOutcome.Drafted && decision.ModelCalls == 1, "The decision row is not a single-call Drafted row.");

        var made = _made!;
        var draftEvents = _hub.GetEvents().Where(e => e.Kind == ActivityKind.Draft).ToList();
        Assert.True(draftEvents.Count == 1
            && draftEvents[0].Summary == DraftTexts.DraftCreated(made.Subject) + " (" + eventSuffixText + ")",
            "The activity log does not show exactly one draft event with the attachment counts.");

        // ai-files: empty, user-only, no link, only temp names.
        Assert.True(LiveAttachmentContent.CountEntries(AiFiles.Folder) == 0, "ai-files is not empty after the draft.");
        Assert.True(AiFiles.PendingRetries == 0, "ai-files still has files waiting for a retry.");
        PrivateFolderTests.AssertUserOnly(AiFiles.Folder);
        Assert.True(new DirectoryInfo(AiFiles.Folder).LinkTarget is null, "ai-files is a link.");
        var seen = WatcherNames;
        Assert.True(seen.All(AiFilePaths.IsTempFileName), "A temp file in ai-files has an unexpected name.");
        if (expectedWatcherNames is { } n)
            Assert.True(seen.Count == n, "A synced file was copied into ai-files.");

        // Usage (SPEC 19.13).
        var ok = (ModelResult.Ok)Guard.LastResult!;
        Assert.True(ok.Usage is { Input: > 0, Output: > 0 }, "The CLI result has no input/output token counts.");
        var t = _fx.Indexes.For("").AiUsageSince(startUtc);
        Assert.True(t.Calls == 1 && t.UnknownCalls == 0 && t.Input == ok.Usage!.Input && t.Output == ok.Usage.Output,
            "No ai_calls row matches the call.");
        Assert.True(_usage.Today().Calls >= 1, "The usage tally of today did not count the call.");

        // Privacy (SPEC 26.8).
        foreach (var secret in secrets)
        {
            Assert.True(!Log.Messages.Any(m => m.Contains(secret, StringComparison.Ordinal))
                && !_hub.GetEvents().Any(e => e.Summary.Contains(secret, StringComparison.Ordinal)),
                "A log line or Activity event names an attachment or its text.");
        }
        foreach (var secret in indexSecrets)
            Assert.True(!LiveAttachmentContent.IndexFilesContain(_fx.Indexes.For("").DbPath, secret),
                "The index holds extracted attachment text.");
    }

    /// <summary>"tokens in n out n, cache_read n|absent, cache_creation n|absent".</summary>
    public static string TokenText(ModelResult.Ok ok)
    {
        static string N(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "absent";
        return $"tokens in {N(ok.Usage?.Input)} out {N(ok.Usage?.Output)}, cache_read {N(ok.Usage?.CacheRead)}, "
            + $"cache_creation {N(ok.Usage?.CacheCreation)}";
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _drafts.Dispose();
        _hub.Dispose();
    }
}
