using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// Draft-only meeting invites against real Outlook (SPEC 22.1 to 22.4, 22.7). The meeting is saved unsent in the own
/// Calendar, tracked, checked without a write, and given a Categories-only save; the note reply draft lands in the own
/// Drafts. The only attendee is the signed-in user. The "sent" branch is simulated by a decision function that treats
/// the real, unsent snapshot as invited; nothing is ever sent. Nothing here prints mail text.
/// </summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("os", "windows")]
public sealed class LiveDraftInviteTests(LiveOutlook fx)
{
    private const string OtherCategory = "clippy-live-other";

    /// <summary>What the test reads back from a meeting. Invited: PidLidFInvited; null = missing/unreadable.</summary>
    private sealed record MeetingFacts(string Subject, DateTime Start, DateTime End, Outlook.OlMeetingStatus Status,
        bool? Invited, string Categories, DateTime LastModified, string ParentFolderId, int RecipientCount);

    private static void Rel(object? o) => LiveDrafts.Rel(o);

    /// <summary>Worker thread only. Releases everything it opens.</summary>
    private static MeetingFacts ReadMeeting(Outlook.Application app, string entryId, string storeId)
    {
        Outlook.NameSpace? session = null;
        Outlook.AppointmentItem? item = null;
        Outlook.MAPIFolder? parent = null;
        Outlook.Recipients? recips = null;
        Outlook.PropertyAccessor? pa = null;
        try
        {
            session = app.Session;
            item = (Outlook.AppointmentItem)session.GetItemFromID(entryId, storeId);
            parent = (Outlook.MAPIFolder)item.Parent;
            recips = item.Recipients;
            bool? invited = null;
            try
            {
                pa = item.PropertyAccessor;
                invited = pa.GetProperty(DraftInvites.FInvitedProperty) is bool b ? b : null;
            }
            catch (COMException)
            {
                invited = null;
            }
            return new MeetingFacts(item.Subject ?? "", item.Start, item.End, item.MeetingStatus, invited,
                item.Categories ?? "", item.LastModificationTime, parent.EntryID, recips.Count);
        }
        finally
        {
            Rel(pa);
            Rel(recips);
            Rel(parent);
            Rel(item);
            Rel(session);
        }
    }

    /// <summary>Sets Categories and calls Save() on that meeting, nothing else. Worker thread only.</summary>
    private static void SetCategories(Outlook.Application app, string entryId, string storeId, string categories)
    {
        Outlook.NameSpace? session = null;
        Outlook.AppointmentItem? item = null;
        try
        {
            session = app.Session;
            item = (Outlook.AppointmentItem)session.GetItemFromID(entryId, storeId);
            item.Categories = categories;
            item.Save();
        }
        finally
        {
            Rel(item);
            Rel(session);
        }
    }

    /// <summary>Session.Categories entry by name; (false, null) when absent. Read-only. Worker thread only.</summary>
    private static (bool Exists, Outlook.OlCategoryColor? Color) MasterCategory(Outlook.Application app, string name)
    {
        Outlook.NameSpace? session = null;
        Outlook.Categories? cats = null;
        try
        {
            session = app.Session;
            cats = session.Categories;
            for (var i = 1; i <= cats.Count; i++)
            {
                Outlook.Category? c = null;
                try
                {
                    c = cats[i];
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return (true, c.Color);
                }
                finally
                {
                    Rel(c);
                }
            }
            return (false, null);
        }
        finally
        {
            Rel(cats);
            Rel(session);
        }
    }

    private static string Collapse(string text) => Regex.Replace(text, @"[\s ]+", " ").Trim();

    [Fact(Timeout = 240_000)]
    public async Task Meeting_draft_is_unsent_categorized_tracked_and_noted()
    {
        var ct = TestContext.Current.CancellationToken;
        await fx.RequireReadyAsync(ct);
        var me = await fx.MeAsync();

        var tz = TimeZoneInfo.Local;
        var s = DateTime.Today.AddDays(22).AddHours(5).AddMinutes(30);
        var e = s.AddMinutes(30);
        var meetingSubject = $"[{fx.RunToken}] draft invite";
        var sourceSubject = $"[{fx.RunToken}] meeting request";
        var expectedNoteSubject = ReplySubject.Normalize(sourceSubject);
        var sep = CultureInfo.CurrentCulture.TextInfo.ListSeparator;

        // Wiring: production classes, except that the tracking file lives under the run's temp root.
        var ownStore = await fx.RunAsync("reading the store id", app => fx.StoreIdOf(app, ""));
        var replyWriter = new OutlookReplyDraftWriter(fx.Worker, fx.Mail, fx.Stores);
        var notes = new MeetingNotes(replyWriter, replyWriter, () => null);
        var store = new DraftInvitesStore(Path.Combine(fx.TempRoot, "draft-invites", DraftInvitesStore.FileName));
        var tracked = new DraftInvites(new OutlookDraftInviteCalendar(fx.Worker), store,
            () => fx.Worker.State == OutlookConnectionState.Available, tz);
        var invites = new MeetingInvites(new OutlookMeetingCalendar(fx.Worker, fx.Stores, () => fx.Settings),
            () => fx.Settings, fx.Hub, tz, tracker: tracked, notes: notes);

        await fx.WithCleanupAsync([""], [Outlook.OlDefaultFolders.olFolderCalendar, Outlook.OlDefaultFolders.olFolderDrafts],
            async scope =>
        {
            var catBefore = await fx.RunAsync("reading the category list", app => MasterCategory(app, DraftInvites.CategoryName));
            var sentBefore = await fx.SnapshotSentAsync("");

            // Synthetic source: self-addressed, unsent, in the own Drafts, no signature.
            var src = await new CreateDraftTool(fx.Settings,
                    new Signatures(_ => null, Path.Combine(fx.TempRoot, "no-signatures")),
                    new OutlookDraftWriter(fx.Worker, fx.Stores), fx.Hub)
                .RunAsync(new CreateDraftArgs(Recipients.Format([me]), sourceSubject,
                    "Automated Clippy's Revenge live test: please find a time. Never sent; deleted by the test.\n" + fx.RunToken), ct);
            scope.Track(new TrackedItem(TrackedKind.Draft, "", src.Id, ownStore, sourceSubject));

            // Reply probe: some profiles can't reply to an unsent item.
            var (probeHtml, probeHr) = await fx.RunAsync("checking Outlook's own reply",
                app => LiveDrafts.TryOwnReply(app, src.Id, ownStore));
            var replyToId = probeHtml is null ? null : src.Id;
            if (replyToId is null)
                fx.Result("draft-invite-note",
                    $"skipped: reply to an unsent item failed (HResult 0x{probeHr.ToString("x8", CultureInfo.InvariantCulture)})");

            // Create.
            MeetingDraftResult result;
            try
            {
                result = await invites.CreateDraftAsync(Recipients.Format([me]), meetingSubject,
                    s.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                    e.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                    "Automated Clippy's Revenge live test. Never sent; deleted by the test.", null, null, replyToId, ct);
            }
            catch (OutlookNotRespondingException)
            {
                Assert.Skip(SkipTexts.NotResponding("create_meeting_draft", 30));
                throw;
            }
            catch (OutlookUnavailableException)
            {
                Assert.Skip(SkipTexts.NotRunning);
                throw;
            }

            var storeId = result.StoreId.Length > 0 ? result.StoreId : ownStore;
            if (result.EntryId.Length > 0)
                scope.Track(new TrackedItem(TrackedKind.Meeting, "", result.EntryId, storeId, meetingSubject));
            DraftFacts? note = null;
            if (result.NoteDraftId is { Length: > 0 } nid)
            {
                scope.Track(new TrackedItem(TrackedKind.Draft, "", nid, ownStore, $"[{fx.RunToken}] " + expectedNoteSubject));
                note = await fx.RunAsync("reading the note", app => LiveDrafts.ReadDraft(fx, app, "", nid, tag: true));
            }
            if (result.EntryId.Length == 0)
                Assert.Fail($"create_meeting_draft returned no EntryId; look for \"[{fx.RunToken}] draft invite\" in the calendar.");

            // Meeting: saved unsent, with the category.
            Assert.Equal("saved_as_draft", result.Status);
            Assert.Equal(DraftInvites.CategoryName, result.Category);
            var attendee = Assert.Single(Recipients.Parse(result.Recipients));
            Assert.Equal(me.Email, attendee.Email, StringComparer.OrdinalIgnoreCase);

            var calendarId = await fx.RunAsync("reading the calendar folder",
                app => fx.DefaultFolderId(app, "", Outlook.OlDefaultFolders.olFolderCalendar));
            var m = await fx.RunAsync("reading the meeting", app => ReadMeeting(app, result.EntryId, storeId));
            Assert.Equal(meetingSubject, m.Subject);
            Assert.Equal(s, m.Start);
            Assert.Equal(e, m.End);
            Assert.Equal(Outlook.OlMeetingStatus.olMeeting, m.Status);
            Assert.True(m.Invited != true, "PidLidFInvited is set: the meeting was invited.");
            Assert.Equal(calendarId, m.ParentFolderId);
            Assert.Contains(DraftInvites.CategoryName, DraftInviteCategories.Split(m.Categories, sep), StringComparer.OrdinalIgnoreCase);

            var catAfter = await fx.RunAsync("reading the category list", app => MasterCategory(app, DraftInvites.CategoryName));
            Assert.True(catAfter.Exists, $"The master category list has no \"{DraftInvites.CategoryName}\" entry.");
            if (!catBefore.Exists)
            {
                Assert.Equal(Outlook.OlCategoryColor.olCategoryColorOrange, catAfter.Color);
                fx.Result("draft-invite-category", "ok (added by this run, orange)");
            }
            else
            {
                fx.Result("draft-invite-category", $"ok (already in the list, colour {catAfter.Color})");
            }

            // Tracking and the unsent branch of the check: no write.
            Assert.Equal(1, tracked.Count);
            var first = Assert.Single(tracked.Pending);
            Assert.Equal(result.EntryId, first.Entry.EntryId);
            Assert.Equal(new DraftInviteDetails(meetingSubject, s, e), first.Details);
            var stored = Assert.Single(new DraftInvitesStore(store.Path).Load());
            Assert.Equal(result.EntryId, stored.EntryId);
            Assert.Equal("", stored.Mailbox);

            var lm0 = m.LastModified;
            IReadOnlyList<PendingDraftInvite> pending;
            try
            {
                pending = await tracked.CheckAsync(WorkPriority.Interactive, ct);
            }
            catch (OutlookNotRespondingException)
            {
                Assert.Skip(SkipTexts.NotResponding("the draft invite check", 30));
                throw;
            }
            catch (OutlookUnavailableException)
            {
                Assert.Skip(SkipTexts.NotRunning);
                throw;
            }
            if (tracked.LastCheckUtc is null)
                Assert.Skip(SkipTexts.NotResponding("the draft invite check", 30));
            var p = Assert.Single(pending);
            Assert.Equal(result.EntryId, p.Entry.EntryId);
            Assert.Equal(meetingSubject, p.Details?.Subject);
            Assert.Equal(s, p.Details?.Start);
            Assert.Equal(e, p.Details?.End);

            var m1 = await fx.RunAsync("reading the meeting again", app => ReadMeeting(app, result.EntryId, storeId));
            Assert.Equal(lm0, m1.LastModified);
            Assert.Contains(DraftInvites.CategoryName, DraftInviteCategories.Split(m1.Categories, sep), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Outlook.OlMeetingStatus.olMeeting, m1.Status);
            Assert.True(m1.Invited != true, "PidLidFInvited is set after the check: the meeting was invited.");

            // Note reply draft.
            if (replyToId is not null)
            {
                Assert.True(result.NoteError is null, "The note was not saved: " + result.NoteError);
                Assert.False(string.IsNullOrEmpty(result.NoteDraftId), "The result has no note_draft_id.");
                Assert.EndsWith(MeetingNoteText.SavedSuffix, result.Message, StringComparison.Ordinal);
                Assert.NotNull(note);
                Assert.Equal(expectedNoteSubject, note.Subject);
                Assert.StartsWith("Re:: ", note.Subject, StringComparison.Ordinal);
                Assert.False(note.Sent);
                Assert.Equal(note.DraftsId, note.ParentId);
                Assert.Equal(DraftMarker.Tool, note.ClippyDraft);

                var label = TimeZoneLabels.Resolve(fx.Settings.TimeZoneLabel, tz, s);
                var expected = MeetingNoteText.Text(s, e, label, meetingSubject, "");
                Assert.True(Collapse(note.Text).Contains(Collapse(expected), StringComparison.Ordinal),
                    "The note text in the reply draft is not the SPEC 22.4 text.");
                fx.Result("draft-invite-note",
                    note.Html.Contains("ffff00", StringComparison.OrdinalIgnoreCase)
                    || note.Html.Contains("yellow", StringComparison.OrdinalIgnoreCase)
                        ? "ok (highlighted)" : "ok (highlight not found in saved HTML)");

                var srcAfter = await fx.RunAsync("reading the source", app => LiveDrafts.ReadDraft(fx, app, "", src.Id));
                Assert.False(srcAfter.Sent);
                Assert.Equal(sourceSubject, srcAfter.Subject);
            }

            // Categories-only save on the unsent meeting (the RemoveCategory write path).
            await fx.RunAsync("adding a second category",
                app => { SetCategories(app, result.EntryId, storeId, $"{OtherCategory}, {DraftInvites.CategoryName}"); return true; });

            DraftInviteSnapshot? seen = null;
            var entry = new DraftInviteEntry(result.EntryId, storeId, "", DateTimeOffset.UtcNow);
            var outcomes = await fx.RunAsync("the forced category removal", app =>
                OutlookCalendar.CheckDraftInvites(app, [entry], snap =>
                {
                    seen = snap;
                    // Simulates "the user sent it". Nothing is sent: only the decision is changed.
                    return DraftInvites.Decide(snap with { Invited = true }, DateTime.Now, sep);
                }, null));

            Assert.NotNull(seen);
            Assert.Equal(DraftInviteLookup.Found, seen.Lookup);
            Assert.False(seen.InDeletedItems);
            Assert.Equal(1, seen.MeetingStatus);
            Assert.True(seen.Invited != true, "The real snapshot says the meeting was invited.");
            Assert.False(seen.OpenInInspector);
            Assert.Contains(DraftInvites.CategoryName, DraftInviteCategories.Split(seen.Categories, sep), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(new DraftInviteDecision(DraftInviteAction.Keep, DraftInviteReason.Unsent),
                DraftInvites.Decide(seen, DateTime.Now, sep));

            var outcome = Assert.Single(outcomes);
            Assert.Equal(DraftInviteAction.RemoveCategory, outcome.Decision.Action);
            Assert.Equal(DraftInviteReason.Sent, outcome.Decision.Reason);
            Assert.Equal(OtherCategory, outcome.Decision.NewCategories);
            Assert.True(outcome.Applied, "The Categories-only save was not applied.");

            var m2 = await fx.RunAsync("reading the meeting after the save", app => ReadMeeting(app, result.EntryId, storeId));
            Assert.Equal(OtherCategory, m2.Categories.Trim());
            Assert.Equal(Outlook.OlMeetingStatus.olMeeting, m2.Status);
            Assert.True(m2.Invited != true, "PidLidFInvited is set after the save: the meeting was invited.");
            Assert.Equal(calendarId, m2.ParentFolderId);
            Assert.Equal(meetingSubject, m2.Subject);
            Assert.Equal(s, m2.Start);
            Assert.Equal(e, m2.End);

            await fx.AssertNothingSentAsync(sentBefore);
            fx.Result("draft-invite-category-save", "ok (still unsent, nothing in Outbox/Sent)");
        });
    }
}
