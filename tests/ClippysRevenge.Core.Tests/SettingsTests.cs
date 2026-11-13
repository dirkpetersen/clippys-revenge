using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public class SettingsTests
{
    static readonly string Profile = OperatingSystem.IsWindows() ? @"C:\Users\alice" : "/home/alice";
    static Settings S() => Settings.CreateDefault(Profile);
    static string Docs => Path.Combine(Profile, "Documents");
    const string PathMsg = @"Must be a full path, like C:\Users\you\Documents.";
    const string NetMsg = "Network and device paths are not allowed.";

    static void AssertOnly(Settings s, params SettingsError[] expected) =>
        Assert.Equal(expected, s.Validate());

    // Defaults
    [Fact]
    public void Defaults_MatchSpec()
    {
        var s = S();
        var att = Path.Combine(Profile, "Downloads", "Clippy's Revenge");
        Assert.Equal(8765, s.Port);
        Assert.Equal(12, s.RetentionMonths);
        Assert.Equal(300, s.SyncIntervalSeconds);
        Assert.Equal(new[] { "Inbox", "Sent Items" }, s.IndexedFolders);
        Assert.Empty(s.SharedMailboxes);
        Assert.Equal(8, s.WorkStartHour);
        Assert.Equal(17, s.WorkEndHour);
        Assert.True(s.BusinessDaysOnly);
        Assert.Equal("", s.TimeZoneLabel);
        Assert.True(s.TentativeIsBusy);
        Assert.Equal(MeetingInviteMode.Send, s.MeetingInviteMode);
        Assert.Equal(120, s.InviteConfirmTimeoutSeconds);
        Assert.Equal(att, s.AttachmentDir);
        Assert.Equal(new[] { att, Docs }, s.AllowedFolders);
        Assert.True(s.StartWithWindows);
        Assert.True(s.NotifyOnErrors);
        Assert.Equal(LogLevel.Information, s.LogLevel);
    }

    [Fact]
    public void Defaults_AreValid() => Assert.Empty(S().Validate());

    [Fact]
    public void ParameterlessCtor_UsesUserProfile() =>
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Clippy's Revenge"),
            new Settings().AttachmentDir);

    [Fact]
    public void Defaults_ListsAreIndependent()
    {
        S().IndexedFolders.Add("X");
        Assert.Equal(2, S().IndexedFolders.Count);
        Assert.Equal(2, Settings.DefaultIndexedFolders.Count);
        new SharedMailbox().IndexedFolders.Add("X");
        Assert.Equal(2, new SharedMailbox().IndexedFolders.Count);
    }

    // Bounds
    [Theory]
    [InlineData(1023, false)]
    [InlineData(1024, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    [InlineData(0, false)]
    public void Port_Bounds(int v, bool ok)
    {
        var s = S(); s.Port = v;
        if (ok) Assert.Empty(s.Validate());
        else AssertOnly(s, new SettingsError("Port", "Must be between 1024 and 65535."));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void RetentionMonths_Bounds(int v, bool ok)
    {
        var s = S(); s.RetentionMonths = v;
        if (ok) Assert.Empty(s.Validate());
        else AssertOnly(s, new SettingsError("RetentionMonths", "Must be between 1 and 120."));
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    public void SyncIntervalSeconds_Bounds(int v, bool ok)
    {
        var s = S(); s.SyncIntervalSeconds = v;
        if (ok) Assert.Empty(s.Validate());
        else AssertOnly(s, new SettingsError("SyncIntervalSeconds", "Must be between 30 and 3600."));
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(900, true)]
    [InlineData(901, false)]
    public void InviteConfirmTimeoutSeconds_Bounds(int v, bool ok)
    {
        var s = S(); s.InviteConfirmTimeoutSeconds = v;
        if (ok) Assert.Empty(s.Validate());
        else AssertOnly(s, new SettingsError("InviteConfirmTimeoutSeconds", "Must be between 30 and 900."));
    }

    [Theory]
    [InlineData(0, 24, null, null)]
    [InlineData(8, 17, null, null)]
    [InlineData(17, 17, "WorkEndHour", "Must be later than the work start hour.")]
    [InlineData(18, 9, "WorkEndHour", "Must be later than the work start hour.")]
    [InlineData(-1, 17, "WorkStartHour", "Must be between 0 and 23.")]
    [InlineData(8, 25, "WorkEndHour", "Must be between 1 and 24.")]
    public void WorkHours(int start, int end, string? field, string? msg)
    {
        var s = S(); s.WorkStartHour = start; s.WorkEndHour = end;
        if (field is null) Assert.Empty(s.Validate());
        else AssertOnly(s, new SettingsError(field, msg!));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("PT", true)]
    [InlineData("1234567890123456", true)]
    [InlineData("12345678901234567", false)]
    [InlineData("P\tT", false)]
    [InlineData(null, false)]
    public void TimeZoneLabel_Rules(string? v, bool ok)
    {
        var s = S(); s.TimeZoneLabel = v!;
        if (ok) Assert.Empty(s.Validate());
        else AssertOnly(s, new SettingsError("TimeZoneLabel", "Must be empty or at most 16 printable characters."));
    }

    [Fact]
    public void EnumsOutOfRange()
    {
        var s = S(); s.MeetingInviteMode = (MeetingInviteMode)7;
        AssertOnly(s, new SettingsError("MeetingInviteMode", "Must be Send, DraftOnly or Disabled."));
        var t = S(); t.LogLevel = (LogLevel)42;
        AssertOnly(t, new SettingsError("LogLevel", "Must be one of Trace, Debug, Information, Warning, Error, Critical, None."));
    }

    [Fact]
    public void IndexedFolders_Rules()
    {
        var s = S(); s.IndexedFolders = [];
        AssertOnly(s, new SettingsError("IndexedFolders", "Pick at least one folder."));
        s = S(); s.IndexedFolders = ["Inbox", " "];
        AssertOnly(s, new SettingsError("IndexedFolders[1]", "Must not be empty."));
        s = S(); s.IndexedFolders = ["Inbox", "inbox "];
        AssertOnly(s, new SettingsError("IndexedFolders[1]", "Listed twice."));
        s = S(); s.IndexedFolders = null!;
        AssertOnly(s, new SettingsError("IndexedFolders", "Pick at least one folder."));
    }

    [Theory]
    [InlineData("", "Must not be empty.")]
    [InlineData(@"\\server\share", NetMsg)]
    [InlineData(@"\\?\C:\x", NetMsg)]
    [InlineData("relative/dir", PathMsg)]
    [InlineData(null, null)]
    public void Paths(string? v, string? msg)
    {
        v ??= Profile;
        var s = S(); s.AttachmentDir = v;
        if (msg is null) Assert.Empty(s.Validate());
        else AssertOnly(s, new SettingsError("AttachmentDir", msg));
        var t = S(); t.AllowedFolders = [v];
        if (msg is null) Assert.Empty(t.Validate());
        else AssertOnly(t, new SettingsError("AllowedFolders[0]", msg));
    }

    [Fact]
    public void AllowedFolders_EmptyValid_NullInvalid()
    {
        var s = S(); s.AllowedFolders = [];
        Assert.Empty(s.Validate());
        s.AllowedFolders = null!;
        AssertOnly(s, new SettingsError("AllowedFolders", "Must not be empty."));
    }

    [Fact]
    public void Validate_CollectsAllErrorsInOrder()
    {
        var s = S(); s.Port = 1; s.RetentionMonths = 0; s.AttachmentDir = "";
        Assert.Equal(["Port", "RetentionMonths", "AttachmentDir"], s.Validate().Select(e => e.Field));
    }

    [Fact]
    public void Validate_DoesNotThrowOnNulls()
    {
        var s = S(); s.IndexedFolders = [null!]; s.SharedMailboxes = [null!];
        AssertOnly(s, new SettingsError("IndexedFolders[0]", "Must not be empty."), new SettingsError("SharedMailboxes[0]", "Must not be empty."));
    }

    // AttachmentDir always allowed
    [Fact]
    public void EffectiveAllowedFolders_IncludesAttachmentDirWhenRemoved()
    {
        var s = S(); s.AllowedFolders = [Docs];
        Assert.Equal(new[] { s.AttachmentDir, Docs }, s.EffectiveAllowedFolders());
    }

    [Fact]
    public void EffectiveAllowedFolders_Dedupes()
    {
        var s = S();
        s.AllowedFolders = [s.AttachmentDir.ToUpperInvariant() + Path.DirectorySeparatorChar, Docs, Docs, " "];
        Assert.Equal(new[] { s.AttachmentDir, Docs }, s.EffectiveAllowedFolders());
    }

    [Fact]
    public void EffectiveAllowedFolders_Defaults()
    {
        var s = S();
        Assert.Equal(new[] { s.AttachmentDir, Docs }, s.EffectiveAllowedFolders());
    }

    // Shared mailboxes
    [Fact]
    public void SharedMailbox_Defaults()
    {
        var m = new SharedMailbox();
        Assert.True(m.Enabled);
        Assert.Equal(new[] { "Inbox", "Sent Items" }, m.IndexedFolders);
        Assert.Null(m.RetentionMonths);
        Assert.False(m.AiDrafts);
        Assert.Equal("", m.DisplayName);
    }

    [Fact]
    public void EffectiveRetentionMonths()
    {
        Assert.Equal(12, new SharedMailbox().EffectiveRetentionMonths(12));
        Assert.Equal(3, new SharedMailbox { RetentionMonths = 3 }.EffectiveRetentionMonths(12));
    }

    [Fact]
    public void MenuLabel()
    {
        Assert.Equal("Helpdesk <helpdesk@example.edu>",
            new SharedMailbox { DisplayName = "Helpdesk", Smtp = "helpdesk@example.edu" }.MenuLabel);
        Assert.Equal("helpdesk@example.edu",
            new SharedMailbox { DisplayName = " ", Smtp = "helpdesk@example.edu" }.MenuLabel);
    }

    [Fact]
    public void AddOrEnable_AddsNew()
    {
        var s = S();
        var m = s.AddOrEnableSharedMailbox(" HelpDesk@Example.edu ", " Helpdesk ");
        Assert.Single(s.SharedMailboxes);
        Assert.Same(m, s.SharedMailboxes[0]);
        Assert.Equal("helpdesk@example.edu", m.Smtp);
        Assert.Equal("Helpdesk", m.DisplayName);
        Assert.True(m.Enabled);
        Assert.Equal(new[] { "Inbox", "Sent Items" }, m.IndexedFolders);
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void AddOrEnable_ReenablesExisting()
    {
        var s = S();
        var existing = new SharedMailbox
        {
            Smtp = "a@x.org", DisplayName = "A", Enabled = false, RetentionMonths = 6, IndexedFolders = ["Inbox"],
        };
        s.SharedMailboxes.Add(existing);
        var m = s.AddOrEnableSharedMailbox("A@X.ORG", null);
        Assert.Same(existing, m);
        Assert.True(m.Enabled);
        Assert.Equal(6, m.RetentionMonths);
        Assert.Equal(new[] { "Inbox" }, m.IndexedFolders);
        Assert.Equal("A", m.DisplayName);
        Assert.Single(s.SharedMailboxes);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("")]
    [InlineData("a@b@c")]
    [InlineData("Name <a@b.org>")]
    public void AddOrEnable_InvalidThrows(string smtp)
    {
        var ex = Assert.Throws<ArgumentException>(() => S().AddOrEnableSharedMailbox(smtp, null));
        Assert.Contains("is not a valid email address.", ex.Message);
    }

    [Fact]
    public void Disable()
    {
        var s = S();
        s.AddOrEnableSharedMailbox("a@x.org", null);
        Assert.True(s.DisableSharedMailbox("A@x.org"));
        Assert.False(s.SharedMailboxes[0].Enabled);
        Assert.Single(s.SharedMailboxes);
        Assert.False(s.DisableSharedMailbox("zzz@x.org"));
    }

    [Fact]
    public void FindSharedMailbox()
    {
        var s = S();
        var m = s.AddOrEnableSharedMailbox("a@x.org", null);
        Assert.Same(m, s.FindSharedMailbox("  A@X.org "));
        Assert.Null(s.FindSharedMailbox("b@x.org"));
    }

    [Fact]
    public void SharedValidation()
    {
        var s = S();
        s.SharedMailboxes = [new SharedMailbox { Smtp = "a@x.org" }, new SharedMailbox { Smtp = "A@X.org " }];
        AssertOnly(s, new SettingsError("SharedMailboxes[1].Smtp", "This mailbox is listed twice."));

        s = S(); s.SharedMailboxes = [new SharedMailbox { Smtp = "bad" }];
        AssertOnly(s, new SettingsError("SharedMailboxes[0].Smtp", "Must be an email address like name@example.org."));

        foreach (var r in new[] { 0, 121 })
        {
            s = S(); s.SharedMailboxes = [new SharedMailbox { Smtp = "a@x.org", RetentionMonths = r }];
            AssertOnly(s, new SettingsError("SharedMailboxes[0].RetentionMonths", "Must be between 1 and 120."));
        }

        s = S(); s.SharedMailboxes = [new SharedMailbox { Smtp = "a@x.org", RetentionMonths = null }];
        Assert.Empty(s.Validate());

        s = S(); s.SharedMailboxes = [new SharedMailbox { Smtp = "a@x.org", IndexedFolders = [] }];
        AssertOnly(s, new SettingsError("SharedMailboxes[0].IndexedFolders", "Pick at least one folder."));
    }

    // JSON
    [Fact]
    public void Json_RoundTrip()
    {
        var s = new Settings
        {
            Port = 9000, RetentionMonths = 24, SyncIntervalSeconds = 60,
            IndexedFolders = ["Inbox", @"Inbox\Projects"],
            SharedMailboxes =
            [
                new SharedMailbox { Smtp = "a@x.org", DisplayName = "A", Enabled = false, IndexedFolders = ["Inbox"], RetentionMonths = 24 },
                new SharedMailbox { Smtp = "b@x.org", DisplayName = "B" },
            ],
            WorkStartHour = 9, WorkEndHour = 18, BusinessDaysOnly = false, TimeZoneLabel = "PT",
            TentativeIsBusy = false, MeetingInviteMode = MeetingInviteMode.DraftOnly,
            InviteConfirmTimeoutSeconds = 60, AttachmentDir = Path.Combine(Profile, "Att"),
            AllowedFolders = [Path.Combine(Profile, "One")], StartWithWindows = false,
            NotifyOnErrors = false, LogLevel = LogLevel.Warning,
        };
        var r = Settings.FromJson(s.ToJson());
        Assert.Equal(s.Port, r.Port);
        Assert.Equal(s.RetentionMonths, r.RetentionMonths);
        Assert.Equal(s.SyncIntervalSeconds, r.SyncIntervalSeconds);
        Assert.Equal(s.IndexedFolders, r.IndexedFolders);
        Assert.Equal(s.WorkStartHour, r.WorkStartHour);
        Assert.Equal(s.WorkEndHour, r.WorkEndHour);
        Assert.Equal(s.BusinessDaysOnly, r.BusinessDaysOnly);
        Assert.Equal(s.TimeZoneLabel, r.TimeZoneLabel);
        Assert.Equal(s.TentativeIsBusy, r.TentativeIsBusy);
        Assert.Equal(s.MeetingInviteMode, r.MeetingInviteMode);
        Assert.Equal(s.InviteConfirmTimeoutSeconds, r.InviteConfirmTimeoutSeconds);
        Assert.Equal(s.AttachmentDir, r.AttachmentDir);
        Assert.Equal(s.AllowedFolders, r.AllowedFolders);
        Assert.Equal(s.StartWithWindows, r.StartWithWindows);
        Assert.Equal(s.NotifyOnErrors, r.NotifyOnErrors);
        Assert.Equal(s.LogLevel, r.LogLevel);
        Assert.Equal(2, r.SharedMailboxes.Count);
        for (int i = 0; i < 2; i++)
        {
            var a = s.SharedMailboxes[i]; var b = r.SharedMailboxes[i];
            Assert.Equal(a.Smtp, b.Smtp);
            Assert.Equal(a.DisplayName, b.DisplayName);
            Assert.Equal(a.Enabled, b.Enabled);
            Assert.Equal(a.IndexedFolders, b.IndexedFolders);
            Assert.Equal(a.RetentionMonths, b.RetentionMonths);
        }
    }

    [Fact]
    public void Json_EnumsAsNames()
    {
        var j = S().ToJson();
        Assert.Contains("\"MeetingInviteMode\": \"Send\"", j);
        Assert.Contains("\"LogLevel\": \"Information\"", j);
    }

    [Fact]
    public void Json_HasNoTokenOrLabel()
    {
        var s = S();
        s.AddOrEnableSharedMailbox("a@x.org", "A");
        var j = s.ToJson();
        Assert.DoesNotContain("token", j, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("menulabel", j, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Json_MissingPropertiesKeepDefaults()
    {
        var s = Settings.FromJson("{\"Port\": 9000}");
        var d = new Settings();
        Assert.Equal(9000, s.Port);
        d.Port = 9000;
        Assert.Equal(d.ToJson(), s.ToJson());
    }

    [Fact]
    public void Json_NullsBecomeDefaults()
    {
        var s = Settings.FromJson(
            "{\"IndexedFolders\":null,\"AllowedFolders\":null,\"SharedMailboxes\":null,\"TimeZoneLabel\":null,\"AttachmentDir\":null}");
        Assert.Equal(new Settings().ToJson(), s.ToJson());
        var t = Settings.FromJson("{\"SharedMailboxes\":[{\"Smtp\":\"a@x.org\",\"IndexedFolders\":null}]}");
        Assert.Equal(new[] { "Inbox", "Sent Items" }, t.SharedMailboxes[0].IndexedFolders);
        Assert.Equal("", t.SharedMailboxes[0].DisplayName);
    }

    [Fact]
    public void Json_LenientInput() =>
        Assert.Equal(9001, Settings.FromJson("{\"port\": 9001, // c\n}").Port);

    [Fact]
    public void Json_UnknownPropertyIgnored() =>
        Assert.Equal(new Settings().ToJson(), Settings.FromJson("{\"Foo\": 1}").ToJson());

    [Theory]
    [InlineData("{")]
    [InlineData("{\"MeetingInviteMode\":\"Bogus\"}")]
    public void Json_Invalid_Throws(string json) =>
        Assert.ThrowsAny<JsonException>(() => Settings.FromJson(json));

    [Fact]
    public void Json_NullDocument() =>
        Assert.Equal(new Settings().ToJson(), Settings.FromJson("null").ToJson());

    [Fact]
    public void Json_DoesNotValidate()
    {
        var s = Settings.FromJson("{\"Port\": 1}");
        Assert.Equal(1, s.Port);
        Assert.Contains(s.Validate(), e => e.Field == "Port");
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var s = S();
        s.AddOrEnableSharedMailbox("a@x.org", "A");
        var c = s.Clone();
        c.IndexedFolders.Add("X");
        c.AllowedFolders.Add("Y");
        c.SharedMailboxes[0].Enabled = false;
        c.SharedMailboxes[0].IndexedFolders.Add("Z");
        Assert.Equal(2, s.IndexedFolders.Count);
        Assert.Equal(2, s.AllowedFolders.Count);
        Assert.True(s.SharedMailboxes[0].Enabled);
        Assert.Equal(2, s.SharedMailboxes[0].IndexedFolders.Count);
    }
}
