using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class StyleSkillFilesTests : IDisposable
{
    private static readonly DateTimeOffset Gen = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Newest = new(2026, 11, 12, 9, 30, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-style-" + Guid.NewGuid().ToString("N"));
    private readonly StylePaths _paths;
    private readonly StyleSkillFiles _files;

    public StyleSkillFilesTests()
    {
        Directory.CreateDirectory(_root);
        _paths = StylePaths.Default(Path.Combine(_root, "home"), Path.Combine(_root, "appdata"));
        _files = new StyleSkillFiles(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string FileText(string body) =>
        StyleSkillText.Render("Dirk", new DateOnly(2026, 11, 13), 30, body);

    private StyleSkillState Write(string body = "APP BODY") => _files.Write(FileText(body), Gen, 143, Newest);

    private static string Sep(string p) => p.Replace('/', Path.DirectorySeparatorChar);

    [Fact]
    public void Default_paths()
    {
        Assert.EndsWith(Sep(".claude/skills/my-email-style/SKILL.md"), _paths.UserSkillFile);
        Assert.EndsWith(Sep("my-email-style/SKILL.md"), _paths.AppCopyFile);
        Assert.EndsWith(Sep("my-email-style/state.json"), _paths.StateFile);
        Assert.Throws<ArgumentException>(() => StylePaths.Default("relative", _root));
        Assert.Throws<ArgumentException>(() => StylePaths.Default(_root, ""));
    }

    [Fact]
    public void Write_creates_files_and_state()
    {
        var text = FileText("APP BODY");
        var state = _files.Write(text, Gen, 143, Newest);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        Assert.Equal(bytes, File.ReadAllBytes(_paths.AppCopyFile));
        Assert.Equal(bytes, File.ReadAllBytes(_paths.UserSkillFile));
        Assert.NotEqual(0xEF, bytes[0]);

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.StateFile));
        Assert.Equal(["hash", "generatedUtc", "sampleCount", "newestSentUtc"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(hash, doc.RootElement.GetProperty("hash").GetString());

        var read = _files.ReadState();
        Assert.Equal(new StyleSkillState(hash, Gen, 143, Newest), read);
        Assert.Equal(read, state);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Write_replaces_existing_files()
    {
        Write("ONE");
        _files.Write(FileText("TWO"), Gen, 5, Newest);
        Assert.Contains("TWO", File.ReadAllText(_paths.UserSkillFile));
        Assert.Contains("TWO", File.ReadAllText(_paths.AppCopyFile));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void IsEdited_follows_the_hash()
    {
        Assert.False(_files.IsEdited());
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.UserSkillFile)!);
        File.WriteAllText(_paths.UserSkillFile, "mine");
        Assert.True(_files.IsEdited());       // no state
        Write();
        Assert.False(_files.IsEdited());
        File.AppendAllText(_paths.UserSkillFile, "x");
        Assert.True(_files.IsEdited());
        Write();
        File.WriteAllText(_paths.StateFile, "garbage");
        Assert.True(_files.IsEdited());
        File.Delete(_paths.UserSkillFile);
        Assert.False(_files.IsEdited());
    }

    [Fact]
    public void ReadState_rejects_bad_state()
    {
        Assert.Null(_files.ReadState());
        var state = Write();
        File.WriteAllText(_paths.StateFile, "garbage");
        Assert.Null(_files.ReadState());
        string Json(string hash, string count) =>
            "{\"hash\":\"" + hash + "\",\"generatedUtc\":\"2026-11-13T10:00:00.0000000+00:00\",\"sampleCount\":" + count + ",\"newestSentUtc\":\"2026-11-12T09:30:00.0000000+00:00\"}";
        File.WriteAllText(_paths.StateFile, Json(state.Hash[..63], "3"));
        Assert.Null(_files.ReadState());
        File.WriteAllText(_paths.StateFile, Json(state.Hash, "-1"));
        Assert.Null(_files.ReadState());
        File.WriteAllText(_paths.StateFile, Json(state.Hash.ToUpperInvariant(), "3"));
        Assert.NotNull(_files.ReadState());
        Assert.False(_files.IsEdited());
    }

    [Fact]
    public void ReadBodyForPrompt_prefers_the_user_file()
    {
        Assert.Equal(StyleSkillText.NoStyleGuideLine, _files.ReadBodyForPrompt());
        Write("APP BODY");
        Assert.Equal("APP BODY", _files.ReadBodyForPrompt());
        File.WriteAllText(_paths.UserSkillFile, "---\nx: y\n---\nMY EDIT");
        Assert.Equal("MY EDIT", _files.ReadBodyForPrompt());
        File.Delete(_paths.UserSkillFile);
        Assert.Equal("APP BODY", _files.ReadBodyForPrompt());
    }

    private void UserFileOfBytes(int total)
    {
        const string head = "---\nx: y\n---\n";
        File.WriteAllText(_paths.UserSkillFile, head + new string('A', total - head.Length));
        Assert.Equal(total, new FileInfo(_paths.UserSkillFile).Length);
    }

    [Fact]
    public void Files_over_64_KiB_are_skipped()
    {
        Write("APP BODY");
        UserFileOfBytes(65_536);
        Assert.StartsWith("AAAA", _files.ReadBodyForPrompt());
        UserFileOfBytes(65_537);
        Assert.Equal("APP BODY", _files.ReadBodyForPrompt());
        File.Delete(_paths.AppCopyFile);
        Assert.Equal(StyleSkillText.NoStyleGuideLine, _files.ReadBodyForPrompt());
    }

    [Fact]
    public void Empty_user_body_wins_over_the_app_copy()
    {
        Write("APP BODY");
        File.WriteAllText(_paths.UserSkillFile, "---\nname: x\n---\n");
        Assert.Equal(StyleSkillText.NoStyleGuideLine, _files.ReadBodyForPrompt());
    }

    [Fact]
    public void A_directory_at_the_user_path_counts_as_missing()
    {
        Write("APP BODY");
        File.Delete(_paths.UserSkillFile);
        Directory.CreateDirectory(_paths.UserSkillFile);
        Assert.Equal("APP BODY", _files.ReadBodyForPrompt());
        Assert.False(_files.IsEdited());
        Assert.Null(_files.UserFileHash());
    }

    [Fact]
    public void Write_failure_leaves_no_state_and_no_temp_files()
    {
        Directory.CreateDirectory(Path.Combine(_root, "home"));
        File.WriteAllText(Path.Combine(_root, "home", ".claude"), "a file, not a folder");
        Assert.ThrowsAny<Exception>(() => Write());   // IOException or UnauthorizedAccessException
        Assert.True(File.Exists(_paths.AppCopyFile));
        Assert.Null(_files.ReadState());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "appdata"), "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void GetStatus_texts()
    {
        var s = _files.GetStatus();
        Assert.Equal(StyleStatusKind.NotGenerated, s.Kind);
        Assert.Equal("Not generated yet", s.Text(TimeZoneInfo.Utc));
        _files.Write(FileText("B"), new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero), 143, Newest);
        s = _files.GetStatus();
        Assert.Equal(StyleStatusKind.Generated, s.Kind);
        Assert.Equal("Generated 2026-11-13 from 143 sent emails", s.Text(TimeZoneInfo.Utc));
        File.AppendAllText(_paths.UserSkillFile, "edit");
        s = _files.GetStatus();
        Assert.Equal(StyleStatusKind.Edited, s.Kind);
        Assert.Equal("Edited by you", s.Text(TimeZoneInfo.Utc));
    }

    [Fact]
    public void ReadSkillText_returns_the_whole_file()
    {
        Assert.Null(_files.ReadSkillText());
        var text = FileText("APP BODY");
        _files.Write(text, Gen, 1, Newest);
        Assert.Equal(text, _files.ReadSkillText());
    }

    private static string Legacy(string body) =>
        "---\nname: my-email-style\ndescription: \"Dirk Petersen's email writing style: greetings, sign-offs, tone, length and phrasing by audience. Use when drafting or replying to email as Dirk Petersen.\"\n---\n<!-- Generated by Clippy's Revenge on 2026-11-13 from 143 sent emails. You can edit this file; Clippy won't replace your edits without asking. -->\n\n" + body + "\n";

    private static readonly DateOnly D = new(2026, 11, 13);

    private StyleSkillState WriteLegacy(string body = "## Greetings\nHi.") => _files.Write(Legacy(body), Gen, 143, Newest);

    [Fact]
    public void Hash_covers_the_section()
    {
        Write();
        var text = File.ReadAllText(_paths.UserSkillFile);
        File.WriteAllText(_paths.UserSkillFile, text.Replace("Don't reorder them.", "Do reorder them."));
        Assert.True(_files.IsEdited());
        Assert.Equal(StyleStatusKind.Edited, _files.GetStatus().Kind);
    }

    [Fact]
    public void Refresh_updates_an_unedited_legacy_skill()
    {
        var old = WriteLegacy();
        Assert.Equal(MeetingSectionRefresh.Updated, _files.RefreshMeetingSection(TimeZoneInfo.Utc));
        var expected = new UTF8Encoding(false).GetBytes(StyleSkillText.Render("Dirk Petersen", D, 143, "## Greetings\nHi.", null));
        Assert.Equal(expected, File.ReadAllBytes(_paths.UserSkillFile));
        Assert.Equal(expected, File.ReadAllBytes(_paths.AppCopyFile));
        var st = _files.ReadState()!;
        Assert.Equal(Gen, st.GeneratedUtc);
        Assert.Equal(143, st.SampleCount);
        Assert.Equal(Newest, st.NewestSentUtc);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), st.Hash);
        Assert.NotEqual(old.Hash, st.Hash);
        Assert.False(_files.IsEdited());
        Assert.Equal("## Greetings\nHi.", _files.ReadBodyForPrompt());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Refresh_twice_is_a_no_op_the_second_time()
    {
        WriteLegacy();
        Assert.Equal(MeetingSectionRefresh.Updated, _files.RefreshMeetingSection(TimeZoneInfo.Utc));
        var user = File.ReadAllBytes(_paths.UserSkillFile);
        var state = File.ReadAllBytes(_paths.StateFile);
        var t1 = File.GetLastWriteTimeUtc(_paths.UserSkillFile);
        var t2 = File.GetLastWriteTimeUtc(_paths.StateFile);
        Assert.Equal(MeetingSectionRefresh.AlreadyCurrent, _files.RefreshMeetingSection(TimeZoneInfo.Utc));
        Assert.Equal(user, File.ReadAllBytes(_paths.UserSkillFile));
        Assert.Equal(state, File.ReadAllBytes(_paths.StateFile));
        Assert.Equal(t1, File.GetLastWriteTimeUtc(_paths.UserSkillFile));
        Assert.Equal(t2, File.GetLastWriteTimeUtc(_paths.StateFile));
    }

    [Fact]
    public void Refresh_of_a_current_skill_writes_nothing()
    {
        Write();
        var user = File.ReadAllBytes(_paths.UserSkillFile);
        var t = File.GetLastWriteTimeUtc(_paths.UserSkillFile);
        Assert.Equal(MeetingSectionRefresh.AlreadyCurrent, _files.RefreshMeetingSection(TimeZoneInfo.Utc));
        Assert.Equal(user, File.ReadAllBytes(_paths.UserSkillFile));
        Assert.Equal(t, File.GetLastWriteTimeUtc(_paths.UserSkillFile));
    }

    [Fact]
    public void Refresh_leaves_an_edited_skill_alone()
    {
        WriteLegacy();
        File.AppendAllText(_paths.UserSkillFile, "x");
        var user = File.ReadAllBytes(_paths.UserSkillFile);
        var app = File.ReadAllBytes(_paths.AppCopyFile);
        var state = File.ReadAllBytes(_paths.StateFile);
        Assert.Equal(MeetingSectionRefresh.NotApplicable, _files.RefreshMeetingSection(TimeZoneInfo.Utc));
        Assert.Equal(user, File.ReadAllBytes(_paths.UserSkillFile));
        Assert.Equal(app, File.ReadAllBytes(_paths.AppCopyFile));
        Assert.Equal(state, File.ReadAllBytes(_paths.StateFile));
    }

    [Fact]
    public void Refresh_needs_both_files_and_a_state()
    {
        WriteLegacy();
        File.Delete(_paths.AppCopyFile);
        var user = File.ReadAllBytes(_paths.UserSkillFile);
        Assert.Equal(MeetingSectionRefresh.NotApplicable, _files.RefreshMeetingSection(TimeZoneInfo.Utc));
        Assert.Equal(user, File.ReadAllBytes(_paths.UserSkillFile));

        File.Delete(_paths.UserSkillFile);
        File.WriteAllText(_paths.AppCopyFile, Legacy("B"));
        Assert.Equal(MeetingSectionRefresh.NotApplicable, _files.RefreshMeetingSection(TimeZoneInfo.Utc));
        Assert.False(File.Exists(_paths.UserSkillFile));

        File.Delete(_paths.StateFile);
        File.WriteAllText(_paths.UserSkillFile, Legacy("B"));
        Assert.Equal(MeetingSectionRefresh.NotApplicable, _files.RefreshMeetingSection(TimeZoneInfo.Utc));
        Assert.Equal(Legacy("B"), File.ReadAllText(_paths.UserSkillFile));
    }

    [Fact]
    public void Refresh_replaces_an_old_section_version_and_keeps_its_phrasing()
    {
        var text = Legacy("BODY") + "\n<!-- clippy:meeting-times v0 -->\n## Offering meeting times\nold text\n\n### How Dirk Petersen offers times\n\nI ask \"Does one of these work?\"\n<!-- /clippy:meeting-times -->\n";
        _files.Write(text, Gen, 143, Newest);
        Assert.Equal(MeetingSectionRefresh.Updated, _files.RefreshMeetingSection(TimeZoneInfo.Utc));
        var now = File.ReadAllText(_paths.UserSkillFile);
        Assert.Equal(StyleSkillText.Render("Dirk Petersen", D, 143, "BODY", "I ask \"Does one of these work?\""), now);
        Assert.Single(now.Split('\n'), l => l.StartsWith(StyleSkillText.MeetingMarkerPrefix, StringComparison.Ordinal));
        Assert.Contains(StyleSkillText.MeetingStartMarker + "\n", now);
        Assert.DoesNotContain("old text", now);
    }

    [Fact]
    public void Refresh_with_an_unparseable_description_uses_the_user()
    {
        _files.Write(Legacy("BODY").Replace("description: \"Dirk Petersen's email writing style: greetings, sign-offs, tone, length and phrasing by audience. Use when drafting or replying to email as Dirk Petersen.\"", "description: \"Writing style.\""), Gen, 143, Newest);
        Assert.Equal(MeetingSectionRefresh.Updated, _files.RefreshMeetingSection(TimeZoneInfo.Utc));
        Assert.Contains("### How the user offers times", File.ReadAllText(_paths.UserSkillFile));
    }

    [Fact]
    public void Refresh_uses_the_zone_for_the_date()
    {
        _files.Write(Legacy("BODY"), new DateTimeOffset(2026, 11, 13, 23, 30, 0, TimeSpan.Zero), 143, Newest);
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        Assert.Equal(MeetingSectionRefresh.Updated, _files.RefreshMeetingSection(zone));
        Assert.Contains(" on 2026-11-14 from 143", File.ReadAllText(_paths.UserSkillFile));
    }

    [Fact]
    public void Refresh_skips_a_result_over_64_KiB()
    {
        WriteLegacy(new string('a', 64_000));
        var user = File.ReadAllBytes(_paths.UserSkillFile);
        var state = File.ReadAllBytes(_paths.StateFile);
        Assert.True(user.Length <= 65_536);
        Assert.Equal(MeetingSectionRefresh.NotApplicable, _files.RefreshMeetingSection(TimeZoneInfo.Utc));
        Assert.Equal(user, File.ReadAllBytes(_paths.UserSkillFile));
        Assert.Equal(state, File.ReadAllBytes(_paths.StateFile));
    }

    [Fact]
    public void ReadBodyForPrompt_drops_the_section_and_keeps_the_rest()
    {
        _files.Write(StyleSkillText.Render("D", D, 1, "BODY"), Gen, 1, Newest);
        File.WriteAllText(_paths.UserSkillFile, StyleSkillText.Render("D", D, 1, "BODY") + "MY NOTE\n");
        Assert.Equal("BODY\n\nMY NOTE", _files.ReadBodyForPrompt());
        File.WriteAllText(_paths.UserSkillFile, "---\nname: x\n---\n" + StyleSkillText.MeetingSection("D", null));
        Assert.Equal(StyleSkillText.NoStyleGuideLine, _files.ReadBodyForPrompt());
    }

    [Fact]
    public void ReadSkillText_keeps_the_section()
    {
        Write();
        var text = _files.ReadSkillText()!;
        Assert.Contains(StyleSkillText.MeetingStartMarker, text);
        Assert.Contains("## Offering meeting times", text);
    }

    [Fact]
    [Trait("os", "windows")]
    public void Refresh_failure_leaves_no_temp_files_and_can_be_retried()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        WriteLegacy();
        var state = File.ReadAllBytes(_paths.StateFile);
        File.SetAttributes(_paths.UserSkillFile, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<Exception>(() => _files.RefreshMeetingSection(TimeZoneInfo.Utc));
            Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
            Assert.Equal(state, File.ReadAllBytes(_paths.StateFile));
        }
        finally
        {
            File.SetAttributes(_paths.UserSkillFile, FileAttributes.Normal);
        }
        Assert.Equal(MeetingSectionRefresh.Updated, _files.RefreshMeetingSection(TimeZoneInfo.Utc));
    }

    [Fact]
    public void Refresh_rejects_a_null_zone()
    {
        Assert.Throws<ArgumentNullException>(() => _files.RefreshMeetingSection(null!));
    }
}
