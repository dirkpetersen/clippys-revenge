using System.Text.Json;

namespace ClippysRevenge.Core.Tests;

public class AiSettingsTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    static readonly string Profile = OperatingSystem.IsWindows() ? @"C:\Users\alice" : "/home/alice";
    static Settings S() => Settings.CreateDefault(Profile);
    static string Tmp(string name) => Path.Combine(Path.GetTempPath(), name);
    const string SenderMsg = "Must be an email address or a domain like example.org.";

    static IReadOnlyList<SettingsError> Errors(Action<AiDraftingSettings> mutate)
    {
        var a = new AiDraftingSettings();
        mutate(a);
        return a.Validate();
    }

    static void AssertValid(bool ok, string field, string message, Action<AiDraftingSettings> mutate)
    {
        var errors = Errors(mutate);
        if (ok) Assert.Empty(errors);
        else Assert.Equal([new SettingsError(field, message)], errors);
    }

    [Fact]
    public void Defaults()
    {
        var a = new AiDraftingSettings();
        Assert.False(a.Enabled);
        Assert.Null(a.ConsentedAtUtc);
        Assert.Null(a.EnabledAtUtc);
        Assert.Equal(AiBackend.ClaudeCode, a.Backend);
        Assert.Equal("claude-opus-5-5", a.Model);
        Assert.Equal("medium", a.Effort);
        Assert.Equal(30, a.DailyCap);
        Assert.Equal(0.6, a.MinConfidence);
        Assert.Equal(2, a.MaxAgeDays);
        Assert.True(a.RequireDirectRecipient);
        Assert.Empty(a.ExcludeSenders);
        Assert.Empty(a.IncludeOnlySenders);
        Assert.Equal(180, a.TimeoutSeconds);
        Assert.Equal("", a.ClaudeCliPath);
        Assert.True(a.AutoRegenerateStyle);
        Assert.Empty(a.Validate());
    }

    [Fact]
    public void SettingsOwnsFreshInstance()
    {
        var a = S();
        var b = S();
        Assert.NotNull(a.AiDrafting);
        Assert.Empty(a.Validate());
        Assert.NotSame(a.AiDrafting, b.AiDrafting);
        Assert.NotSame(a.AiDrafting.ExcludeSenders, b.AiDrafting.ExcludeSenders);
        Assert.NotSame(a.AiDrafting.IncludeOnlySenders, b.AiDrafting.IncludeOnlySenders);
    }

    [Fact]
    public void ConsentRule()
    {
        var expected = new SettingsError("AiDrafting.Enabled", AiDraftingSettings.ConsentRequiredMessage);
        var s = S();
        s.AiDrafting.Enabled = true;
        Assert.Equal([expected], s.AiDrafting.Validate());
        Assert.Equal([expected], s.Validate());
        s.AiDrafting.ConsentedAtUtc = Now;
        Assert.Empty(s.Validate());
        s.AiDrafting.Enabled = false;
        s.AiDrafting.ConsentedAtUtc = null;
        Assert.Empty(s.Validate());
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(500, true)] [InlineData(501, false)]
    public void DailyCap(int v, bool ok) =>
        AssertValid(ok, "AiDrafting.DailyCap", "Must be between 1 and 500.", a => a.DailyCap = v);

    [Theory]
    [InlineData(-0.01, false)] [InlineData(0.0, true)] [InlineData(0.6, true)] [InlineData(1.0, true)]
    [InlineData(1.0001, false)] [InlineData(double.NaN, false)] [InlineData(double.PositiveInfinity, false)]
    public void MinConfidence(double v, bool ok) =>
        AssertValid(ok, "AiDrafting.MinConfidence", "Must be between 0 and 1.", a => a.MinConfidence = v);

    [Theory]
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(14, true)] [InlineData(15, false)]
    public void MaxAgeDays(int v, bool ok) =>
        AssertValid(ok, "AiDrafting.MaxAgeDays", "Must be between 1 and 14.", a => a.MaxAgeDays = v);

    [Theory]
    [InlineData(29, false)] [InlineData(30, true)] [InlineData(900, true)] [InlineData(901, false)]
    public void TimeoutSeconds(int v, bool ok) =>
        AssertValid(ok, "AiDrafting.TimeoutSeconds", "Must be between 30 and 900.", a => a.TimeoutSeconds = v);

    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-sonnet-4-6[1m]", true)]
    [InlineData("a", true)]
    [InlineData("A.b_c-d", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("-p", false)]
    [InlineData("--bare", false)]
    [InlineData("1model", false)]
    [InlineData("claude opus", false)]
    [InlineData("claude-opus-5-5\n", false)]
    [InlineData("claude/x", false)]
    [InlineData("claude\"x", false)]
    public void Model(string? v, bool ok) =>
        AssertValid(ok, "AiDrafting.Model",
            "Must be a model name like claude-opus-5-5: up to 64 letters, digits, '.', '_', '-', '[' or ']', starting with a letter.",
            a => a.Model = v!);

    [Fact]
    public void ModelLength()
    {
        Assert.Empty(Errors(a => a.Model = new string('a', 64)));
        Assert.Single(Errors(a => a.Model = new string('a', 65)));
    }

    [Theory]
    [InlineData("low", true)] [InlineData("medium", true)] [InlineData("high", true)]
    [InlineData("xhigh", true)] [InlineData("max", true)]
    [InlineData("Medium", false)] [InlineData("", false)] [InlineData(null, false)]
    [InlineData("none", false)] [InlineData(" medium", false)]
    public void Effort(string? v, bool ok) =>
        AssertValid(ok, "AiDrafting.Effort", "Must be low, medium, high, xhigh or max.", a => a.Effort = v!);

    [Fact]
    public void BackendUndefined() =>
        Assert.Equal([new SettingsError("AiDrafting.Backend", "Must be ClaudeCode or ApiKey.")],
            Errors(a => a.Backend = (AiBackend)9));

    [Theory]
    [InlineData("a@x.org", true)] [InlineData("x.org", true)] [InlineData("@x.org", true)]
    [InlineData("mail.x.org", true)] [InlineData(" X.ORG ", true)]
    [InlineData("x", false)] [InlineData("@", false)] [InlineData("a@", false)] [InlineData("@@x.org", false)]
    [InlineData("a@b@c.org", false)] [InlineData("x..org", false)] [InlineData(".x.org", false)]
    [InlineData("x.org.", false)] [InlineData("-x.org", false)] [InlineData("x-.org", false)]
    [InlineData("*.x.org", false)] [InlineData("x.org/", false)] [InlineData("ex ample.org", false)]
    [InlineData("b\u00fccher.de", false)]
    public void SenderEntries(string entry, bool ok)
    {
        foreach (var list in new[] { "ExcludeSenders", "IncludeOnlySenders" })
        {
            var errors = Errors(a =>
            {
                var l = list == "ExcludeSenders" ? a.ExcludeSenders : a.IncludeOnlySenders;
                l.Add(entry);
            });
            if (ok) Assert.Empty(errors);
            else Assert.Equal([new SettingsError($"AiDrafting.{list}[0]", SenderMsg)], errors);
        }
    }

    [Fact]
    public void SenderEntry_LongLabelRejected() =>
        Assert.Single(Errors(a => a.ExcludeSenders.Add(new string('a', 64) + ".org")));

    [Fact]
    public void BlankAndNullEntries()
    {
        var errors = Errors(a => a.ExcludeSenders = ["", null!, "  "]);
        Assert.Equal(
            [
                new SettingsError("AiDrafting.ExcludeSenders[0]", "Must not be empty."),
                new SettingsError("AiDrafting.ExcludeSenders[1]", "Must not be empty."),
                new SettingsError("AiDrafting.ExcludeSenders[2]", "Must not be empty."),
            ], errors);
    }

    [Fact]
    public void NullList() =>
        Assert.Equal([new SettingsError("AiDrafting.IncludeOnlySenders", "Must not be empty.")],
            Errors(a => a.IncludeOnlySenders = null!));

    [Fact]
    public void EntryCountCap()
    {
        Assert.Empty(Errors(a => a.ExcludeSenders = [.. Enumerable.Repeat("x.org", 200)]));
        Assert.Equal([new SettingsError("AiDrafting.ExcludeSenders", "At most 200 entries.")],
            Errors(a => a.ExcludeSenders = [.. Enumerable.Repeat("x.org", 201)]));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("claude.exe", true)]
    [InlineData("CLAUDE.EXE", true)]
    [InlineData("cli.js", true)]
    public void ClaudeCliPath_Valid(string name, bool _)
    {
        var path = name.Trim().Length == 0 ? name : Tmp(name);
        Assert.Empty(Errors(a => a.ClaudeCliPath = path));
    }

    [Theory]
    [InlineData("claude.cmd")] [InlineData("claude.bat")] [InlineData("claude.ps1")] [InlineData("claude")]
    public void ClaudeCliPath_BadExtension(string name) =>
        Assert.Equal([new SettingsError("AiDrafting.ClaudeCliPath", "Must be the path of claude.exe or of Claude Code's cli.js.")],
            Errors(a => a.ClaudeCliPath = Tmp(name)));

    [Fact]
    public void ClaudeCliPath_ForwardSlashRejected() =>
        Assert.Equal([new SettingsError("AiDrafting.ClaudeCliPath", "Use backslashes in the Claude Code path.")],
            Errors(a => a.ClaudeCliPath = @"C:/Users/you/claude.exe"));

    [Fact]
    public void ClaudeCliPath_PathErrors()
    {
        Assert.Equal([new SettingsError("AiDrafting.ClaudeCliPath", @"Must be a full path, like C:\Users\you\Documents.")],
            Errors(a => a.ClaudeCliPath = "claude.exe"));
        Assert.Equal([new SettingsError("AiDrafting.ClaudeCliPath", "Network and device paths are not allowed.")],
            Errors(a => a.ClaudeCliPath = @"\\server\share\claude.exe"));
    }

    [Fact]
    public void ErrorOrder()
    {
        var errors = Errors(a =>
        {
            a.Enabled = true;
            a.Backend = (AiBackend)9;
            a.Model = "";
            a.Effort = "";
            a.DailyCap = 0;
            a.MinConfidence = 2;
            a.MaxAgeDays = 0;
            a.ExcludeSenders = ["x"];
            a.IncludeOnlySenders = ["y"];
            a.TimeoutSeconds = 0;
            a.ClaudeCliPath = "rel.exe";
        });
        Assert.Equal(
            [
                "AiDrafting.Enabled", "AiDrafting.Backend", "AiDrafting.Model", "AiDrafting.Effort", "AiDrafting.DailyCap",
                "AiDrafting.MinConfidence", "AiDrafting.MaxAgeDays", "AiDrafting.ExcludeSenders[0]",
                "AiDrafting.IncludeOnlySenders[0]", "AiDrafting.TimeoutSeconds", "AiDrafting.ClaudeCliPath",
            ], errors.Select(e => e.Field));
    }

    [Fact]
    public void SettingsValidateOrderAndNull()
    {
        var s = S();
        s.Port = 1;
        s.AiDrafting.DailyCap = 0;
        Assert.Equal(["Port", "AiDrafting.DailyCap"], s.Validate().Select(e => e.Field));
        var n = S();
        n.AiDrafting = null!;
        Assert.Equal([new SettingsError("AiDrafting", "Must not be empty.")], n.Validate());
    }

    [Fact]
    public void RecordConsent()
    {
        var a = new AiDraftingSettings();
        a.RecordConsent(new DateTimeOffset(2026, 11, 13, 14, 0, 0, TimeSpan.FromHours(2)));
        Assert.Equal(Now, a.ConsentedAtUtc);
        Assert.Equal(TimeSpan.Zero, a.ConsentedAtUtc!.Value.Offset);
        a.RecordConsent(Now.AddHours(5));
        Assert.Equal(Now, a.ConsentedAtUtc);
    }

    [Fact]
    public void SetEnabled_WithoutConsent()
    {
        var a = new AiDraftingSettings();
        var ex = Assert.Throws<InvalidOperationException>(() => a.SetEnabled(true, Now));
        Assert.Equal(AiDraftingSettings.ConsentRequiredMessage, ex.Message);
        Assert.False(a.Enabled);
        Assert.Null(a.EnabledAtUtc);
        a.SetEnabled(false, Now);
    }

    [Fact]
    public void SetEnabled_WithConsent()
    {
        var a = new AiDraftingSettings();
        a.RecordConsent(Now);
        a.SetEnabled(true, Now);
        Assert.True(a.Enabled);
        Assert.Equal(Now, a.EnabledAtUtc);
        a.SetEnabled(true, Now.AddHours(1));
        Assert.Equal(Now, a.EnabledAtUtc);
        a.SetEnabled(false, Now.AddHours(2));
        Assert.False(a.Enabled);
        Assert.Equal(Now, a.EnabledAtUtc);
        a.SetEnabled(true, Now.AddHours(3));
        Assert.Equal(Now.AddHours(3), a.EnabledAtUtc);
    }

    [Fact]
    public void CandidateCutoff()
    {
        var a = new AiDraftingSettings { MaxAgeDays = 2, EnabledAtUtc = Now.AddHours(-1) };
        Assert.Equal(Now.AddHours(-1), a.CandidateCutoff(Now));
        a.EnabledAtUtc = Now.AddDays(-5);
        Assert.Equal(Now.AddDays(-2), a.CandidateCutoff(Now));
        a.EnabledAtUtc = null;
        Assert.Equal(Now, a.CandidateCutoff(Now));
        Assert.Equal(TimeSpan.Zero, a.CandidateCutoff(Now.ToOffset(TimeSpan.FromHours(2))).Offset);
    }

    [Fact]
    public void SenderPattern_TryParse()
    {
        Assert.True(SenderPattern.TryParse("Boss@X.org", out var p));
        Assert.Equal(new SenderPattern("boss@x.org", false), p);
        Assert.True(SenderPattern.TryParse("@Example.ORG", out p));
        Assert.Equal(new SenderPattern("example.org", true), p);
        Assert.True(SenderPattern.TryParse(" example.org ", out p));
        Assert.Equal(new SenderPattern("example.org", true), p);
        Assert.False(SenderPattern.TryParse(null, out _));
    }

    [Fact]
    public void SenderPattern_DomainMatches()
    {
        SenderPattern.TryParse("example.org", out var p);
        foreach (var yes in new[] { "a@example.org", "A@EXAMPLE.ORG", "a@mail.example.org", " a@example.org " })
            Assert.True(p.Matches(yes), yes);
        foreach (var no in new[] { "a@badexample.org", "a@example.org.evil.com", "example.org", "" })
            Assert.False(p.Matches(no), no);
        Assert.False(p.Matches(null));
    }

    [Fact]
    public void SenderPattern_AddressMatches()
    {
        SenderPattern.TryParse("boss@x.org", out var p);
        Assert.True(p.Matches("Boss@X.org"));
        Assert.False(p.Matches("boss@mail.x.org"));
        Assert.False(p.Matches("other@x.org"));
    }

    // ---- JSON ----

    [Fact]
    public void Json_Defaults()
    {
        var s = S();
        s.SharedMailboxes.Add(new SharedMailbox { Smtp = "a@x.org" });
        var json = s.ToJson();
        Assert.Contains("\"AiDrafting\": {", json);
        Assert.Contains("\"Backend\": \"ClaudeCode\"", json);
        Assert.Contains("\"Effort\": \"medium\"", json);
        Assert.Contains("\"AiDrafts\": false", json);
    }

    [Fact]
    public void Json_RoundTrip()
    {
        var s = S();
        var a = s.AiDrafting;
        a.Enabled = true;
        a.ConsentedAtUtc = Now.AddDays(-1);
        a.EnabledAtUtc = Now;
        a.Backend = AiBackend.ApiKey;
        a.Model = "claude-sonnet-4-6[1m]";
        a.Effort = "max";
        a.DailyCap = 7;
        a.MinConfidence = 0.75;
        a.MaxAgeDays = 5;
        a.RequireDirectRecipient = false;
        a.ExcludeSenders = ["a@x.org", "y.org"];
        a.IncludeOnlySenders = ["b@x.org", "z.org"];
        a.TimeoutSeconds = 60;
        a.ClaudeCliPath = Tmp("cli.js");
        a.AutoRegenerateStyle = false;
        s.SharedMailboxes.Add(new SharedMailbox { Smtp = "a@x.org", AiDrafts = true });
        var back = Settings.FromJson(s.ToJson());
        Assert.Equal(s.ToJson(), back.ToJson());
        Assert.Equal(Now.AddDays(-1), back.AiDrafting.ConsentedAtUtc);
        Assert.Equal(Now, back.AiDrafting.EnabledAtUtc);
        Assert.Equal(AiBackend.ApiKey, back.AiDrafting.Backend);
        Assert.Equal(["b@x.org", "z.org"], back.AiDrafting.IncludeOnlySenders);
        Assert.True(back.SharedMailboxes[0].AiDrafts);
        Assert.Empty(back.Validate());
    }

    [Fact]
    public void Json_MissingOrNull()
    {
        Assert.Equal(new Settings().AiDrafting.Model, Settings.FromJson("{}").AiDrafting.Model);
        Assert.NotNull(Settings.FromJson("{\"AiDrafting\":null}").AiDrafting);
        var s = Settings.FromJson(
            "{\"AiDrafting\":{\"Model\":null,\"Effort\":null,\"ExcludeSenders\":null,\"IncludeOnlySenders\":null,\"ClaudeCliPath\":null}}");
        Assert.Equal(new Settings().ToJson(), s.ToJson());
    }

    [Fact]
    public void Json_LenientAndBadEnum()
    {
        var s = Settings.FromJson("{\"AiDrafting\":{\"enabled\":false,\"Unknown\":1}}");
        Assert.False(s.AiDrafting.Enabled);
        Assert.False(Settings.FromJson("{\"SharedMailboxes\":[{\"Smtp\":\"a@x.org\"}]}").SharedMailboxes[0].AiDrafts);
        Assert.Throws<JsonException>(() => Settings.FromJson("{\"AiDrafting\":{\"Backend\":\"Nope\"}}"));
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var s = S();
        s.AiDrafting.ExcludeSenders = ["x.org"];
        var c = s.Clone();
        Assert.NotSame(s.AiDrafting, c.AiDrafting);
        Assert.NotSame(s.AiDrafting.ExcludeSenders, c.AiDrafting.ExcludeSenders);
        Assert.NotSame(s.AiDrafting.IncludeOnlySenders, c.AiDrafting.IncludeOnlySenders);
        Assert.Equal(s.ToJson(), c.ToJson());
    }

    [Fact]
    public void CopyFrom_ReplacesInstance()
    {
        var src = S();
        src.AiDrafting.DailyCap = 9;
        var target = S();
        var old = target.AiDrafting;
        target.CopyFrom(src);
        Assert.NotSame(src.AiDrafting, target.AiDrafting);
        Assert.NotSame(old, target.AiDrafting);
        Assert.Equal(9, target.AiDrafting.DailyCap);
        Assert.Equal(30, old.DailyCap);
    }

    [Fact]
    public void AddOrEnableSharedMailbox_KeepsAiDrafts()
    {
        var s = S();
        var sm = s.AddOrEnableSharedMailbox("a@x.org", null);
        Assert.False(sm.AiDrafts);
        sm.AiDrafts = true;
        s.DisableSharedMailbox("a@x.org");
        Assert.True(s.AddOrEnableSharedMailbox("a@x.org", null).AiDrafts);
    }
}
