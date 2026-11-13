namespace ClippysRevenge.Core.Tests;

/// <summary>Opt-in: talks to the real GitHub. Skipped unless CLIPPY_LIVE_GITHUB=1. Downloads no exe.</summary>
[Trait("live", "github")]
public sealed class UpdateClientLiveTests
{
    [Fact]
    public async Task Latest_release_and_its_hash_from_the_real_github()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CLIPPY_LIVE_GITHUB") == "1",
            "Set CLIPPY_LIVE_GITHUB=1 to run.");

        using var client = new UpdateClient(SemVersion.Parse("0.0.1"));
        var result = await client.GetLatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Ok, result.Outcome);
        var release = result.Release!;
        Assert.True(SemVersion.TryParseReleaseTag(release.Tag, out _));
        Assert.True(release.ExeSize > ReleaseParser.MinExeBytes);

        // Follows the real redirect, so it proves the host allow-list.
        var hash = await client.GetExpectedHashAsync(release, TestContext.Current.CancellationToken);
        Assert.True(UpdateHash.IsSha256Hex(hash));
    }
}
