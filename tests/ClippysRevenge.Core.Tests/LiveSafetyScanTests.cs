namespace ClippysRevenge.Core.Tests;

/// <summary>
/// Not a live test: runs in CI on Linux and Windows. Scans the live Outlook test sources and the test
/// project file for the hard rules (CLAUDE.md): no Send, no launch, no Outlook instantiation, and meetings
/// only through create_meeting_draft. The M10.1 scan of src/ lives in SafetySourceScanTests and is not affected.
/// </summary>
public class LiveSafetyScanTests
{
    private static readonly string[] Forbidden =
    [
        ".Send(", ".Display(", "OutlookLauncher", "new Outlook.Application", "new Application(",
        "Activator", "CoCreateInstance",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClippysRevenge.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "ClippysRevenge.slnx not found above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    private static string LiveDir() => Path.Combine(RepoRoot(), "tests", "ClippysRevenge.Core.Tests", "Live");

    [Fact]
    public void Live_tests_never_send_or_launch()
    {
        var files = Directory.EnumerateFiles(LiveDir(), "*.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        var hits = new List<string>();
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (var token in Forbidden)
                if (text.Contains(token, StringComparison.Ordinal))
                    hits.Add($"{Path.GetFileName(f)} contains \"{token}\"");
        }
        Assert.True(hits.Count == 0, "Live test sources break a hard rule:" + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void Linked_app_sources_exclude_launcher()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "ClippysRevenge.Core.Tests", "ClippysRevenge.Core.Tests.csproj"));
        foreach (var token in new[] { "OutlookLauncher.cs", "Program.cs", "McpTools", "Tray\\", "Tray/" })
            Assert.DoesNotContain(token, csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_tests_create_meeting_drafts_only()
    {
        var calendar = File.ReadAllText(Path.Combine(LiveDir(), "LiveCalendarTests.cs"));
        Assert.Contains("CreateDraftAsync", calendar, StringComparison.Ordinal);
        Assert.DoesNotContain("SendAsync", calendar, StringComparison.Ordinal);
        var fixture = File.ReadAllText(Path.Combine(LiveDir(), "LiveOutlook.cs"));
        Assert.Contains("MeetingInviteMode.DraftOnly", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_cleanup_deletes_only_recorded_token_items()
    {
        var fixture = File.ReadAllText(Path.Combine(LiveDir(), "LiveOutlook.cs"));
        Assert.Contains("GetItemFromID(tracked.EntryId, tracked.StoreId)", fixture, StringComparison.Ordinal);
        Assert.Contains("StartsWith(\"[\" + RunToken + \"] \"", fixture, StringComparison.Ordinal);
    }
}
