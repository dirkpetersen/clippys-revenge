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
}
