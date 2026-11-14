using System.Globalization;
using System.Reflection;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public class SettingsEditorTests
{
    private static Settings Live(params (string smtp, bool enabled)[] shared)
    {
        var s = Settings.CreateDefault(Path.GetTempPath());
        foreach (var (smtp, enabled) in shared)
        {
            var sm = s.AddOrEnableSharedMailbox(smtp, null);
            sm.Enabled = enabled;
        }
        return s;
    }

    [Fact]
    public void CopyFrom_CoversEveryProperty()
    {
        var src = Settings.CreateDefault(Path.GetTempPath());
        var handled = 0;
        src.Port++; handled++;
        src.RetentionMonths++; handled++;
        src.SyncIntervalSeconds++; handled++;
        src.IndexedFolders = [.. src.IndexedFolders, "Archive"]; handled++;
        src.SharedMailboxes = [new SharedMailbox { Smtp = "a@x.edu", DisplayName = "A", RetentionMonths = 3 }]; handled++;
        src.WorkStartHour++; handled++;
        src.WorkEndHour--; handled++;
        src.BusinessDaysOnly = !src.BusinessDaysOnly; handled++;
        src.TimeZoneLabel = "PT"; handled++;
        src.TentativeIsBusy = !src.TentativeIsBusy; handled++;
        src.MeetingInviteMode = MeetingInviteMode.Disabled; handled++;
        src.AttachmentDir += "2"; handled++;
        src.AllowedFolders = [.. src.AllowedFolders, Path.Combine(Path.GetTempPath(), "more")]; handled++;
        src.StartWithWindows = !src.StartWithWindows; handled++;
        src.NotifyOnErrors = !src.NotifyOnErrors; handled++;
        src.LogLevel = LogLevel.Debug; handled++;
        src.Updates = UpdateMode.Notify; handled++;
        src.AiDrafting = new AiDraftingSettings { DailyCap = 5, ExcludeSenders = ["x.org"] }; handled++;

        var count = typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Count(p => p.CanRead && p.CanWrite);
        Assert.Equal(count, handled);

        var target = Settings.CreateDefault(Path.GetTempPath());
        target.CopyFrom(src);
        Assert.Equal(src.ToJson(), target.ToJson());
    }

    [Fact]
    public void Rebase_AdoptsAiDraftsOfNewMailbox()
    {
        var live = Live();
        var e = new SettingsEditor(live);
        live.AddOrEnableSharedMailbox("b@x.edu", null).AiDrafts = true;
        e.Rebase(live);
        Assert.True(e.Draft.FindSharedMailbox("b@x.edu")!.AiDrafts);
    }

    [Fact]
    public void CopyFrom_DeepAndDoesNotMutateOldLists()
    {
        var target = Settings.CreateDefault(Path.GetTempPath());
        var old = target.AllowedFolders;
        var oldItems = old.ToArray();
        var src = Settings.CreateDefault(Path.GetTempPath());
        src.AllowedFolders = [Path.Combine(Path.GetTempPath(), "other")];
        src.AddOrEnableSharedMailbox("a@x.edu", "A");
        target.CopyFrom(src);
        Assert.Equal(oldItems, old);
        Assert.NotSame(src.AllowedFolders, target.AllowedFolders);
        Assert.NotSame(src.SharedMailboxes[0], target.SharedMailboxes[0]);
        Assert.Throws<ArgumentNullException>(() => target.CopyFrom(null!));
    }

    [Fact]
    public void NewEditor_NoChanges()
    {
        var live = Live();
        var e = new SettingsEditor(live);
        Assert.False(e.Changes(live).Any);
        Assert.Empty(e.Errors());
    }

    [Fact]
    public void Editing_DoesNotTouchLive()
    {
        var live = Live();
        var e = new SettingsEditor(live);
        e.Draft.Port = 9000;
        Assert.Equal(8765, live.Port);
    }

    [Fact]
    public void TrySetInt_Garbage()
    {
        var e = new SettingsEditor(Live());
        Assert.False(e.TrySetInt("Port", "80a", v => e.Draft.Port = v));
        var errs = e.Errors();
        Assert.Contains(new SettingsError("Port", "Must be a whole number."), errs);
        Assert.DoesNotContain(errs, x => x.Message.StartsWith("Must be between"));
        Assert.True(e.TrySetInt("Port", "9000", v => e.Draft.Port = v));
        Assert.Empty(e.Errors());
        Assert.Equal(9000, e.Draft.Port);
    }

    [Fact]
    public void TrySetInt_OutOfRange()
    {
        var e = new SettingsEditor(Live());
        Assert.True(e.TrySetInt("Port", "80", v => e.Draft.Port = v));
        Assert.Contains(new SettingsError("Port", "Must be between 1024 and 65535."), e.Errors());
    }

    [Fact]
    public void TrySetInt_TrimsAndInvariant()
    {
        var e = new SettingsEditor(Live());
        Assert.True(e.TrySetInt("Port", " 9000 ", v => e.Draft.Port = v));
        Assert.Equal(9000, e.Draft.Port);
        Assert.False(e.TrySetInt("Port", "9,000", v => e.Draft.Port = v));
        Assert.Equal(9000, e.Draft.Port);
    }

    [Fact]
    public void PortChanged_And_Breaks()
    {
        var live = Live();
        var e = new SettingsEditor(live);
        e.Draft.Port = 9000;
        var c = e.Changes(live);
        Assert.True(c.PortChanged);
        Assert.True(c.BreaksClaudeConfig);
    }

    [Fact]
    public void RegenerateToken()
    {
        var live = Live();
        var e = new SettingsEditor(live);
        e.RegenerateToken();
        var first = e.PendingToken!;
        Assert.Matches("^[0-9a-f]{64}$", first);
        e.RegenerateToken();
        Assert.NotEqual(first, e.PendingToken);
        var c = e.Changes(live);
        Assert.True(c.TokenChanged);
        Assert.True(c.Any);
        Assert.True(c.BreaksClaudeConfig);
        Assert.DoesNotContain(e.PendingToken!, e.ToString());
    }

    [Fact]
    public void RetentionChanged_Global()
    {
        var live = Live();
        var e = new SettingsEditor(live);
        e.Draft.RetentionMonths = 6;
        Assert.True(e.Changes(live).RetentionChanged);
    }

    [Fact]
    public void RetentionChanged_PerMailbox()
    {
        var live = Live(("a@x.edu", true));
        var e = new SettingsEditor(live);
        e.Draft.SharedMailboxes[0].RetentionMonths = 3;
        Assert.True(e.Changes(live).RetentionChanged);
    }

    [Fact]
    public void SyncInterval_IsSyncPlanNotRetention()
    {
        var live = Live();
        var e = new SettingsEditor(live);
        e.Draft.SyncIntervalSeconds = 60;
        var c = e.Changes(live);
        Assert.True(c.SyncPlanChanged);
        Assert.False(c.RetentionChanged);
    }

    [Fact]
    public void FolderOrderAndCase_NotAChange()
    {
        var live = Live();
        live.IndexedFolders = ["Inbox", "Sent Items"];
        var e = new SettingsEditor(live);
        e.Draft.IndexedFolders = ["sent items", "Inbox"];
        Assert.False(e.Changes(live).SyncPlanChanged);
        e.Draft.IndexedFolders.Add(@"Inbox\Projects");
        Assert.True(e.Changes(live).SyncPlanChanged);
    }

    [Fact]
    public void AddMailbox_New()
    {
        var live = Live();
        var e = new SettingsEditor(live);
        e.AddMailbox("Help@X.edu", "Help");
        var c = e.Changes(live);
        Assert.Equal(["help@x.edu"], c.EnabledMailboxes);
        Assert.True(c.SyncPlanChanged);
        Assert.Contains(e.MailboxChoices(), x => x.Key == "help@x.edu" && x.Label == "Help <help@x.edu>");
    }

    [Fact]
    public void RemoveMailbox_Keep()
    {
        var live = Live(("a@x.edu", true));
        var e = new SettingsEditor(live);
        Assert.True(e.RemoveMailbox("a@x.edu", false));
        var c = e.Changes(live);
        Assert.Equal(["a@x.edu"], c.DisabledMailboxes);
        Assert.Empty(c.DeleteIndexes);
        Assert.False(e.Draft.SharedMailboxes.Single().Enabled);
        Assert.DoesNotContain(e.MailboxChoices(), x => x.Key == "a@x.edu");
    }

    [Fact]
    public void RemoveMailbox_Delete_ThenReAdd()
    {
        var live = Live(("a@x.edu", true));
        var e = new SettingsEditor(live);
        e.RemoveMailbox("a@x.edu", true);
        Assert.Equal(["a@x.edu"], e.Changes(live).DeleteIndexes);
        e.AddMailbox("a@x.edu", null);
        var c = e.Changes(live);
        Assert.Empty(c.DeleteIndexes);
        Assert.Empty(c.DisabledMailboxes);
    }

    [Fact]
    public void RemoveMailbox_Unknown() =>
        Assert.False(new SettingsEditor(Live()).RemoveMailbox("nobody@x.edu", true));

    [Fact]
    public void MailboxChoices_Order()
    {
        var e = new SettingsEditor(Live(("a@x.edu", true), ("b@x.edu", false), ("c@x.edu", true)));
        var keys = e.MailboxChoices();
        Assert.Equal(["", "a@x.edu", "c@x.edu"], keys.Select(k => k.Key));
        Assert.Equal("My mailbox", keys[0].Label);
    }

    [Theory]
    [InlineData("Port", SettingsTab.Connection)]
    [InlineData("RetentionMonths", SettingsTab.Index)]
    [InlineData("IndexedFolders[1]", SettingsTab.Index)]
    [InlineData("SharedMailboxes[0].Smtp", SettingsTab.Index)]
    [InlineData("WorkEndHour", SettingsTab.Scheduling)]
    [InlineData("TimeZoneLabel", SettingsTab.Scheduling)]
    [InlineData("MeetingInviteMode", SettingsTab.Scheduling)]
    [InlineData("AllowedFolders[2]", SettingsTab.General)]
    [InlineData("LogLevel", SettingsTab.General)]
    [InlineData("", SettingsTab.General)]
    public void TabFor(string field, SettingsTab tab) => Assert.Equal(tab, SettingsEditor.TabFor(field));

    [Theory]
    [InlineData("SharedMailboxes[3].Smtp", 3)]
    [InlineData("SharedMailboxes[12]", 12)]
    [InlineData("Port", null)]
    [InlineData("SharedMailboxes[x].Smtp", null)]
    public void SharedMailboxIndexFor(string field, int? expected) =>
        Assert.Equal(expected, SettingsEditor.SharedMailboxIndexFor(field));

    [Fact]
    public void Texts()
    {
        Assert.StartsWith("Change the port from 8765 to 9000?", SettingsTexts.PortChangeQuestion(8765, 9000));
        Assert.Equal("Stop indexing Help <h@x.edu>?", SettingsTexts.StopIndexingQuestion("Help <h@x.edu>"));
        var masked = SettingsTexts.MaskedMcpAddCommand(8765);
        Assert.Contains("http://127.0.0.1:8765/mcp", masked);
        Assert.Contains("Bearer ••••••••", masked);
        Assert.DoesNotMatch("[0-9a-f]{64}", masked);
        var wsl = SettingsTexts.WslHelp(9000);
        Assert.Contains("networkingMode=mirrored", wsl);
        Assert.Contains("wsl --shutdown", wsl);
        Assert.Contains("http://127.0.0.1:9000/mcp", wsl);
        Assert.Equal(7, SettingsTexts.LogLevels.Count);
        Assert.Equal(Enum.GetValues<LogLevel>(), SettingsTexts.LogLevels);
    }

    private static IndexStatus Status(DateTimeOffset? oldest, DateTimeOffset? newest, DateTimeOffset? last) =>
        new(48210, new Dictionary<string, long>(), oldest, newest, 537_290_342, last, true);

    [Fact]
    public void IndexStatsText_Format()
    {
        var now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var st = Status(new DateTimeOffset(2025, 11, 12, 1, 0, 0, TimeSpan.Zero), now, now.AddMinutes(-2));
        const string expected = "48,210 emails · oldest 2025-11-12 · newest 2026-11-12 · 512.4 MB · last sync 2 min ago";
        Assert.Equal(expected, IndexStatsText.Format(st, now, TimeZoneInfo.Utc));
        Assert.Equal("Not indexed yet.", IndexStatsText.Format(null, now, TimeZoneInfo.Utc));
        var empty = IndexStatsText.Format(Status(null, null, null), now, TimeZoneInfo.Utc);
        Assert.Contains("oldest — · newest —", empty);
        Assert.EndsWith("last sync never", empty);

        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(expected, IndexStatsText.Format(st, now, TimeZoneInfo.Utc));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1_572_864L, "1.5 MB")]
    [InlineData(1_288_490_189L, "1.2 GB")]
    public void Bytes(long b, string expected) => Assert.Equal(expected, IndexStatsText.Bytes(b));

    [Fact]
    public void FolderTree_Build()
    {
        var roots = FolderTree.Build(["Inbox", "Archive", @"Inbox\Projects", "Sent Items", @"Archive\2025"],
            ["Inbox", @"archive\2025"]);
        Assert.Equal(["Inbox", "Sent Items", "Archive"], roots.Select(r => r.Name));
        Assert.True(roots[0].Checked);
        var projects = Assert.Single(roots[0].Children);
        Assert.Equal("Projects", projects.Name);
        Assert.False(projects.Checked);
        var y = Assert.Single(roots[2].Children);
        Assert.True(y.Checked);
        Assert.Equal(@"Archive\2025", y.Path);
        Assert.All(roots, r => Assert.False(r.Missing));
    }

    [Fact]
    public void FolderTree_Build_MissingConfigured()
    {
        var roots = FolderTree.Build(["Inbox"], ["Inbox", @"Old\Stuff"]);
        var old = roots.Single(r => r.Name == "Old");
        Assert.True(old.Missing);
        Assert.False(old.Checked);
        var stuff = Assert.Single(old.Children);
        Assert.True(stuff.Missing);
        Assert.True(stuff.Checked);
    }

    [Fact]
    public void FolderTree_Build_NullAvailable()
    {
        var roots = FolderTree.Build(null, ["Inbox", "Sent Items", "  ", @"Archive\2025"]);
        Assert.Equal(["Inbox", "Sent Items", "Archive"], roots.Select(r => r.Name));
        Assert.False(roots[2].Checked);
        Assert.True(roots[2].Children.Single().Checked);
        Assert.All(roots, r => Assert.False(r.Missing));
        Assert.All(roots[2].Children, r => Assert.False(r.Missing));
    }
}
