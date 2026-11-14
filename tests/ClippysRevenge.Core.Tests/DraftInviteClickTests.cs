using System.Runtime.InteropServices;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public sealed class DraftInviteClickTests
{
    private sealed class FakeWindow(DraftInviteOpenResult result = DraftInviteOpenResult.Opened, Exception? throws = null) : IDraftInviteWindow
    {
        public List<DraftInviteEntry> Calls { get; } = [];

        public Task<DraftInviteOpenResult> OpenAsync(DraftInviteEntry entry, CancellationToken ct)
        {
            Calls.Add(entry);
            if (throws is not null) throw throws;
            return Task.FromResult(result);
        }
    }

    private static readonly DraftInviteEntry Entry = new("E-SECRET-1", "S-SECRET", "help@x.edu", DateTimeOffset.UtcNow);

    private readonly List<string> _forgot = [];
    private readonly CapturingLog _log = new();

    private bool Forget(string id)
    {
        _forgot.Add(id);
        return true;
    }

    private Task<string?> Click(FakeWindow w, Func<string, bool>? forget = null) =>
        DraftInviteClick.OpenAsync(w, Entry, forget ?? Forget, _log);

    [Fact]
    public async Task Opened_no_balloon_no_forget()
    {
        var w = new FakeWindow();
        Assert.Null(await Click(w));
        Assert.Empty(_forgot);
        Assert.Same(Entry, Assert.Single(w.Calls));
    }

    [Fact]
    public async Task Gone_forgets_and_says_so()
    {
        Assert.Equal(DraftInviteMenu.GoneText, await Click(new FakeWindow(DraftInviteOpenResult.Gone)));
        Assert.Equal(["E-SECRET-1"], _forgot);
    }

    [Fact]
    public async Task Gone_forget_throwing_is_swallowed()
    {
        var text = await Click(new FakeWindow(DraftInviteOpenResult.Gone), _ => throw new InvalidOperationException("boom"));
        Assert.Equal(DraftInviteMenu.GoneText, text);
        Assert.Contains(_log.Lines, l => l.Contains("InvalidOperationException"));
    }

    [Fact]
    public async Task Unavailable()
    {
        Assert.Equal(BalloonTexts.OutlookUnavailable, await Click(new FakeWindow(throws: new OutlookUnavailableException())));
        Assert.Empty(_forgot);
    }

    [Fact]
    public async Task Not_responding()
    {
        Assert.Equal(BalloonTexts.OutlookNotResponding, await Click(new FakeWindow(throws: new OutlookNotRespondingException())));
        Assert.Empty(_forgot);
    }

    [Fact]
    public async Task Canceled()
    {
        Assert.Null(await Click(new FakeWindow(throws: new OperationCanceledException())));
        Assert.Empty(_forgot);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Other_failure(bool com)
    {
        Exception ex = com ? new COMException("secret subject", unchecked((int)0x80004005)) : new UnauthorizedAccessException("secret subject");
        Assert.Equal(DraftInviteMenu.OpenFailedText, await Click(new FakeWindow(throws: ex)));
        Assert.Empty(_forgot);
        Assert.Contains(_log.Lines, l => l.Contains(ex.GetType().Name));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("secret"));
    }

    [Fact]
    public async Task Logs_have_no_ids()
    {
        await Click(new FakeWindow());
        await Click(new FakeWindow(DraftInviteOpenResult.Gone));
        await Click(new FakeWindow(throws: new OutlookUnavailableException()));
        await Click(new FakeWindow(throws: new OutlookNotRespondingException()));
        await Click(new FakeWindow(throws: new COMException("x", unchecked((int)0x80004005))));
        Assert.NotEmpty(_log.Lines);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("E-SECRET-1") || l.Contains("help@x.edu") || l.Contains("S-SECRET"));
    }

    [Fact]
    public async Task Null_arguments_throw()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => DraftInviteClick.OpenAsync(null!, Entry, Forget, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DraftInviteClick.OpenAsync(new FakeWindow(), null!, Forget, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DraftInviteClick.OpenAsync(new FakeWindow(), Entry, null!, null, TestContext.Current.CancellationToken));
    }
}
