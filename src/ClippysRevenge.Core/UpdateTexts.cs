using System.Globalization;

namespace ClippysRevenge.Core;

/// <summary>Exact texts (SPEC 6.7, 20.10). Versions are passed without "v"; the texts add it.</summary>
public static class UpdateTexts
{
    // Failure reasons (SPEC 20.10)
    public const string ReasonTimeout = "GitHub didn't answer within 15 s";
    public static string ReasonHttp(int code) => string.Create(CultureInfo.InvariantCulture, $"GitHub answered HTTP {code}");
    public const string ReasonRateLimited = "GitHub's rate limit was reached; try again later";
    public static string ReasonNetwork(string exceptionTypeName) => $"network error ({exceptionTypeName})";
    public const string ReasonNoRelease = "no release found";
    public const string ReasonBadJson = "the release information couldn't be read";
    public const string ReasonNotFullRelease = "the latest release isn't a full release";
    public const string ReasonNoAssets = "the release has no usable ClippysRevenge.exe or SHA256SUMS.txt";
    public const string ReasonSumsMissing = "SHA256SUMS.txt has no entry for ClippysRevenge.exe";
    public const string ReasonHashMismatch = "the checksum doesn't match";
    public const string ReasonSizeMismatch = "the file size doesn't match the release";
    public const string ReasonNotExe = "the file isn't a Windows program";
    public const string ReasonVersionMismatch = "the downloaded program has a different version than the release";
    public static string ReasonRedirect(string host) => $"a redirect went to {host}, which isn't allowed";
    public const string ReasonDownloadTimeout = "the download took too long";
    public const string ReasonOldInUse = "the previous version is still in use; restart Windows and try again";
    public static string ReasonSaveFailed(string exceptionTypeName) => $"the file couldn't be saved ({exceptionTypeName})";
    public const string ReasonStagedMissing = "the downloaded update is missing";
    public static string ReasonReplaceFailed(string exceptionTypeName) =>
        $"the installed program couldn't be replaced ({exceptionTypeName})";
    public const string ReasonNotInstalled = "this copy isn't in the install folder";

    // Update events (ActivityKind.Update, SPEC 20.10)
    public static string EventAvailable(string v) => $"update check: v{v} available";
    public static string EventUpToDate(string running) => $"update check: up to date (v{running})";
    public static string EventCheckFailed(string reason) => $"update check failed: {reason}";
    public static string EventStaged(string v) => $"update v{v} downloaded and verified";
    public static string EventDownloadFailed(string reason) => $"update download failed: {reason}";
    public static string EventInstalling(string v) => $"installing update v{v}";
    public static string EventInstallFailed(string reason) => $"update failed: {reason}";
    public static string EventUpdated(string from, string to) => $"updated from v{from} to v{to}";
    public static string EventRolledBack(string failed, string back) => $"update v{failed} didn't start; back on v{back}";

    // About window (SPEC 6.7)
    public const string Title = "About Clippy's Revenge";
    public const string ProductName = "Clippy's Revenge";
    public const string Checking = "Checking for updates…";

    public static string Downloading(string v, int percent) =>
        string.Create(CultureInfo.InvariantCulture, $"Downloading v{v}… {Math.Clamp(percent, 0, 100)} %");

    public const string Verifying = "Verifying the download…";
    public const string WaitingForIdle = "Waiting for Clippy to finish what it's doing…";
    public static string Installing(string v) => $"Installing v{v}…";
    public const string Busy = "Clippy is busy. Try again in a minute.";
    public static string CheckFailed(string reason) => $"✗ Couldn't check for updates: {reason}";
    public static string DownloadFailed(string reason) => $"✗ The update couldn't be downloaded: {reason}";
    public static string InstallFailed(string reason) => $"✗ The update couldn't be installed: {reason}";
    public const string NoCheckYet = "No update check yet.";
    public const string UpToDate = "You have the latest version.";

    public static string FailedBefore(string failed, string running) =>
        $"v{failed} didn't start on this PC, so Clippy's Revenge went back to v{running}.";

    public static string AvailableNotInstalled(string v) => $"v{v} is available. Download it from the release page.";

    public static string Ready(string v) =>
        $"v{v} is downloaded. It installs the next time Clippy's Revenge starts, or press Update now.";

    public static string Available(string v, DateTimeOffset? publishedUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (publishedUtc is not { } p)
        {
            return $"v{v} is available.";
        }
        var local = TimeZoneInfo.ConvertTime(p, zone);
        return string.Create(CultureInfo.InvariantCulture, $"v{v} is available (released {local:yyyy-MM-dd}).");
    }

    public const string UpdateNow = "Update now";
    public static string UpdateTo(string v) => $"Update to v{v}";
    public const string CheckButton = "Check for updates";
    public const string CloseButton = "Close";
    public const string DocsLink = "Documentation";
    public const string NotesLink = "Release notes";

    /// <exception cref="ArgumentOutOfRangeException">mode is not a defined value.</exception>
    public static string ModeLine(UpdateMode mode) => mode switch
    {
        UpdateMode.Install => "Updates: install automatically",
        UpdateMode.Notify => "Updates: only tell me",
        UpdateMode.Off => "Updates: don't check",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public const string LastCheckNever = "Last checked: never";

    public static string LastCheck(DateTimeOffset checkedUtc, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(checkedUtc, zone);
        return string.Create(CultureInfo.InvariantCulture,
            $"Last checked: {local:yyyy-MM-dd HH:mm} ({TrayText.RelativePast(checkedUtc, now)})");
    }

    public static string VersionLine(AppVersionInfo running)
    {
        ArgumentNullException.ThrowIfNull(running);
        var text = "Version " + running.Version;
        return running.ShortCommit is { } c ? text + " (commit " + c + ")" : text;
    }

    public static string RuntimeLine(string framework, string os) => $"{framework} · {os}";
    public static string InstalledIn(string folder) => $"Installed in {folder}";
    public static string RunningFrom(string folder) => $"Running from {folder}. Only the installed copy updates itself.";
}

public enum UpdateActivity { None, Checking, Downloading, Verifying, WaitingForIdle, Installing }

/// <summary>Snapshot for the About window (SPEC 6.7). Built by UpdateService (M12.2).</summary>
/// <param name="RunningDir">Folder of the running exe.</param>
/// <param name="ActivityVersion">The version being downloaded or installed (for the texts), else null.</param>
/// <param name="ActionMessage">The result of the last manual action, already formatted, or null.</param>
public sealed record UpdateStatus(
    AppVersionInfo Running, bool Installed, string RunningDir, UpdateMode Mode,
    DateTimeOffset? LastCheckUtc, string? LastError,
    string? LatestTag, DateTimeOffset? LatestPublishedUtc,
    string? StagedVersion, string? FailedVersion,
    UpdateActivity Activity, int Percent, string? ActivityVersion, string? ActionMessage);

public sealed record AboutModel(
    string Title, string ProductName, string VersionLine, string RuntimeLine, string LocationLine,
    string ModeLine, string LastCheckLine, string StatusLine,
    bool UpdateNowEnabled, string UpdateNowText, bool CheckEnabled,
    string DocsUrl, string NotesUrl);

public static class AboutView
{
    /// <exception cref="ArgumentNullException">status, frameworkDescription, osDescription or zone is null.</exception>
    public static AboutModel Build(UpdateStatus status, string frameworkDescription, string osDescription,
        DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(frameworkDescription);
        ArgumentNullException.ThrowIfNull(osDescription);
        ArgumentNullException.ThrowIfNull(zone);

        var running = status.Running.Version;
        SemVersion.TryParseReleaseTag(status.LatestTag, out var latest);
        var newer = latest is not null && UpdatePolicy.IsNewer(latest, running);

        return new AboutModel(
            UpdateTexts.Title,
            UpdateTexts.ProductName,
            UpdateTexts.VersionLine(status.Running),
            UpdateTexts.RuntimeLine(frameworkDescription, osDescription),
            status.Installed ? UpdateTexts.InstalledIn(status.RunningDir) : UpdateTexts.RunningFrom(status.RunningDir),
            UpdateTexts.ModeLine(status.Mode),
            status.LastCheckUtc is { } last ? UpdateTexts.LastCheck(last, now, zone) : UpdateTexts.LastCheckNever,
            StatusLine(status, latest, zone),
            status.Installed && status.Activity == UpdateActivity.None && newer,
            newer ? UpdateTexts.UpdateTo(latest!.ToString()) : UpdateTexts.UpdateNow,
            status.Activity == UpdateActivity.None,
            UpdateEndpoints.DocsUrl,
            latest is not null ? UpdateEndpoints.ReleaseNotesUrl(status.LatestTag!) : UpdateEndpoints.ReleasesPage);
    }

    private static string StatusLine(UpdateStatus s, SemVersion? latest, TimeZoneInfo zone)
    {
        switch (s.Activity)
        {
            case UpdateActivity.Checking:
                return UpdateTexts.Checking;
            case UpdateActivity.Downloading:
                return UpdateTexts.Downloading(s.ActivityVersion ?? "?", s.Percent);
            case UpdateActivity.Verifying:
                return UpdateTexts.Verifying;
            case UpdateActivity.WaitingForIdle:
                return UpdateTexts.WaitingForIdle;
            case UpdateActivity.Installing:
                return UpdateTexts.Installing(s.ActivityVersion ?? "?");
        }
        if (s.ActionMessage is not null)
        {
            return s.ActionMessage;
        }
        if (s.LastError is not null)
        {
            return UpdateTexts.CheckFailed(s.LastError);
        }
        if (latest is null)
        {
            return UpdateTexts.NoCheckYet;
        }
        var v = latest.ToString();
        if (latest <= s.Running.Version)
        {
            return UpdateTexts.UpToDate;
        }
        if (v == s.FailedVersion)
        {
            return UpdateTexts.FailedBefore(v, s.Running.Version.ToString());
        }
        if (!s.Installed)
        {
            return UpdateTexts.AvailableNotInstalled(v);
        }
        if (s.StagedVersion == v)
        {
            return UpdateTexts.Ready(v);
        }
        return UpdateTexts.Available(v, s.LatestPublishedUtc, zone);
    }
}
