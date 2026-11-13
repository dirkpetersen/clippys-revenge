using Xunit;

namespace ClippysRevenge.Core.Tests;

public class QueueInviteConfirmerTests
{
    private sealed class CapturePresenter : IInviteConfirmPresenter
    {
        public InviteConfirmView? View { get; private set; }

        public Task<InviteChoice?> ShowAsync(InviteConfirmView view, DateTimeOffset deadline, CancellationToken dismiss)
        {
            View = view;
            return Task.FromResult<InviteChoice?>(InviteChoice.Cancel);
        }
    }

    private static TimeZoneInfo? Pacific()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
        catch (TimeZoneNotFoundException)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
            catch (TimeZoneNotFoundException) { return null; }
        }
    }

    private static async Task<InviteConfirmView> Run(Settings settings, TimeZoneInfo tz, DateTime start)
    {
        var presenter = new CapturePresenter();
        using var hub = new ActivityHub(TimeProvider.System);
        using var queue = new InviteConfirmQueue(presenter, hub, TimeProvider.System);
        var confirmer = new QueueInviteConfirmer(queue, () => settings, tz);
        var preview = new InvitePreview("Me", "me@x.edu", "", "S", start, start.AddHours(2), null, false, false,
            [new InviteAttendee("", "a@x.edu", false)], "b");
        await confirmer.ConfirmAsync(preview, TimeSpan.FromSeconds(30), CancellationToken.None);
        return presenter.View!;
    }

    [Fact]
    public async Task Label_IsDaylightAwarePerSlot()
    {
        var tz = Pacific();
        if (tz is null) return;
        var view = await Run(new Settings(), tz, new DateTime(2026, 7, 14, 13, 0, 0));
        Assert.Contains("PT", view.WhenFull);
        Assert.DoesNotContain("Standard", view.WhenFull);
        Assert.DoesNotContain("Standard", view.WhenShort ?? "");
    }

    [Fact]
    public async Task Label_HonorsSettingsOverride()
    {
        var view = await Run(new Settings { TimeZoneLabel = "Home" }, TimeZoneInfo.Utc, new DateTime(2026, 7, 14, 13, 0, 0));
        Assert.Contains("Home", view.WhenFull);
    }
}
