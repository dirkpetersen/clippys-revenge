namespace ClippysRevenge.Core.Tests;

public class AboutViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    private const string Fw = ".NET 10.0.1";
    private const string Os = "Microsoft Windows 10.0.26100";
    private const string Dir = @"C:\P\ClippysRevenge";

    private static UpdateStatus Status(Func<UpdateStatus, UpdateStatus>? change = null)
    {
        var s = new UpdateStatus(
            AppVersionInfo.Parse("0.0.5+ce89762560811e819f6ecb5385df93622cee00e5"), true, Dir, UpdateMode.Install,
            null, null, null, null, null, null, UpdateActivity.None, 0, null, null);
        return change is null ? s : change(s);
    }

    private static AboutModel Build(UpdateStatus s, TimeZoneInfo? zone = null) =>
        AboutView.Build(s, Fw, Os, Now, zone ?? TimeZoneInfo.Utc);

    [Fact]
    public void HeaderLines()
    {
        var m = Build(Status());
        Assert.Equal("About Clippy's Revenge", m.Title);
        Assert.Equal("Clippy's Revenge", m.ProductName);
        Assert.Equal("Version 0.0.5 (commit ce89762)", m.VersionLine);
        Assert.Equal(".NET 10.0.1 · Microsoft Windows 10.0.26100", m.RuntimeLine);
        Assert.Equal(@"Installed in C:\P\ClippysRevenge", m.LocationLine);
        Assert.Equal(@"Running from C:\P\ClippysRevenge. Only the installed copy updates itself.",
            Build(Status(s => s with { Installed = false })).LocationLine);
        Assert.Equal("Version 0.0.5",
            Build(Status(s => s with { Running = new AppVersionInfo(SemVersion.Parse("0.0.5"), null) })).VersionLine);
    }

    [Fact]
    public void ModeLines()
    {
        Assert.Equal("Updates: install automatically", Build(Status(s => s with { Mode = UpdateMode.Install })).ModeLine);
        Assert.Equal("Updates: only tell me", Build(Status(s => s with { Mode = UpdateMode.Notify })).ModeLine);
        Assert.Equal("Updates: don't check", Build(Status(s => s with { Mode = UpdateMode.Off })).ModeLine);
        Assert.Throws<ArgumentOutOfRangeException>(() => UpdateTexts.ModeLine((UpdateMode)9));
    }

    [Fact]
    public void LastCheckLines()
    {
        Assert.Equal("Last checked: never", Build(Status()).LastCheckLine);
        var checkedAt = new DateTimeOffset(2026, 11, 17, 14, 59, 0, TimeSpan.Zero);
        Assert.Equal("Last checked: 2026-11-17 14:59 (3 d ago)",
            Build(Status(s => s with { LastCheckUtc = checkedAt })).LastCheckLine);
    }

    private static string StatusLine(Func<UpdateStatus, UpdateStatus> change, TimeZoneInfo? zone = null) =>
        Build(Status(change), zone).StatusLine;

    [Fact]
    public void Status_Activities_WinOverEverything()
    {
        Func<UpdateStatus, UpdateStatus> noisy = s => s with
        {
            ActionMessage = "x", LastError = "y", LatestTag = "v0.0.6",
        };
        Assert.Equal("Checking for updates…",
            StatusLine(s => noisy(s) with { Activity = UpdateActivity.Checking }));
        Assert.Equal("Downloading v0.0.6… 45 %",
            StatusLine(s => noisy(s) with { Activity = UpdateActivity.Downloading, Percent = 45, ActivityVersion = "0.0.6" }));
        Assert.Equal("Downloading v0.0.6… 100 %",
            StatusLine(s => s with { Activity = UpdateActivity.Downloading, Percent = 140, ActivityVersion = "0.0.6" }));
        Assert.Equal("Downloading v?… 0 %",
            StatusLine(s => s with { Activity = UpdateActivity.Downloading, Percent = -5 }));
        Assert.Equal("Verifying the download…", StatusLine(s => s with { Activity = UpdateActivity.Verifying }));
        Assert.Equal("Waiting for Clippy to finish what it's doing…",
            StatusLine(s => s with { Activity = UpdateActivity.WaitingForIdle }));
        Assert.Equal("Installing v0.0.6…",
            StatusLine(s => s with { Activity = UpdateActivity.Installing, ActivityVersion = "0.0.6" }));
    }

    [Fact]
    public void Status_MessagesAndErrors()
    {
        Assert.Equal("Clippy is busy. Try again in a minute.",
            StatusLine(s => s with { ActionMessage = UpdateTexts.Busy, LastError = "e", LatestTag = "v0.0.6" }));
        Assert.Equal("✗ The last update attempt failed: no release found",
            StatusLine(s => s with { LastError = "no release found", LatestTag = "v0.0.6" }));
        Assert.Equal("No update check yet.", StatusLine(s => s));
        Assert.Equal("No update check yet.", StatusLine(s => s with { LatestTag = "garbage" }));
    }

    [Fact]
    public void Status_Versions()
    {
        Assert.Equal("You have the latest version.", StatusLine(s => s with { LatestTag = "v0.0.5" }));
        Assert.Equal("You have the latest version.", StatusLine(s => s with { LatestTag = "v0.0.4" }));
        Assert.Equal("v0.0.6 didn't start on this PC, so Clippy's Revenge went back to v0.0.5.",
            StatusLine(s => s with { LatestTag = "v0.0.6", FailedVersion = "0.0.6" }));
        Assert.Equal("v0.0.6 is available. Download it from the release page.",
            StatusLine(s => s with { LatestTag = "v0.0.6", Installed = false }));
        Assert.Equal("v0.0.6 is downloaded. It installs the next time Clippy's Revenge starts, or press Update now.",
            StatusLine(s => s with { LatestTag = "v0.0.6", StagedVersion = "0.0.6" }));
        Assert.Equal("v0.0.6 is available.", StatusLine(s => s with { LatestTag = "v0.0.6" }));
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        Assert.Equal("v0.0.6 is available (released 2026-11-20).",
            StatusLine(s => s with
            {
                LatestTag = "v0.0.6",
                LatestPublishedUtc = new DateTimeOffset(2026, 11, 19, 23, 30, 0, TimeSpan.Zero),
            }, zone));
    }

    [Fact]
    public void UpdateNow_And_Check()
    {
        var newer = Build(Status(s => s with { LatestTag = "v0.0.6" }));
        Assert.True(newer.UpdateNowEnabled);
        Assert.Equal("Update to v0.0.6", newer.UpdateNowText);
        Assert.True(newer.CheckEnabled);

        var failedButNewer = Build(Status(s => s with { LatestTag = "v0.0.6", FailedVersion = "0.0.6" }));
        Assert.True(failedButNewer.UpdateNowEnabled);

        Assert.False(Build(Status(s => s with { LatestTag = "v0.0.6", Installed = false })).UpdateNowEnabled);
        var busy = Build(Status(s => s with { LatestTag = "v0.0.6", Activity = UpdateActivity.Checking }));
        Assert.False(busy.UpdateNowEnabled);
        Assert.False(busy.CheckEnabled);

        var same = Build(Status(s => s with { LatestTag = "v0.0.5" }));
        Assert.False(same.UpdateNowEnabled);
        Assert.Equal("Update now", same.UpdateNowText);
        var lower = Build(Status(s => s with { LatestTag = "v0.0.4" }));
        Assert.False(lower.UpdateNowEnabled);
        Assert.Equal("Update now", lower.UpdateNowText);
        Assert.Equal("Update now", Build(Status()).UpdateNowText);
    }

    [Fact]
    public void Urls()
    {
        var m = Build(Status(s => s with { LatestTag = "v0.0.6" }));
        Assert.Equal("https://github.com/dirkpetersen/clippys-revenge/releases/tag/v0.0.6", m.NotesUrl);
        Assert.Equal("https://dirkpetersen.github.io/clippys-revenge/", m.DocsUrl);
        Assert.Equal("https://github.com/dirkpetersen/clippys-revenge/releases", Build(Status()).NotesUrl);
    }

    [Fact]
    public void Build_NullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => AboutView.Build(null!, Fw, Os, Now, TimeZoneInfo.Utc));
        Assert.Throws<ArgumentNullException>(() => AboutView.Build(Status(), null!, Os, Now, TimeZoneInfo.Utc));
        Assert.Throws<ArgumentNullException>(() => AboutView.Build(Status(), Fw, null!, Now, TimeZoneInfo.Utc));
        Assert.Throws<ArgumentNullException>(() => AboutView.Build(Status(), Fw, Os, Now, null!));
    }
}
