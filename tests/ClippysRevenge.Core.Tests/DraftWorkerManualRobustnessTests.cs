using System.Runtime.InteropServices;
using static ClippysRevenge.Core.Tests.FakeDraftModel;

namespace ClippysRevenge.Core.Tests;

public class DraftWorkerManualRobustnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DraftHarness Make()
    {
        var h = new DraftHarness();
        h.Mailbox.Selected = new SelectedEmail("E1", "S1");
        h.Mailbox.Live = new LiveEmail("", new IndexedEmail
        {
            EntryId = "E1",
            Folder = "Inbox",
            Subject = "Budget",
            SenderEmail = "ann@contoso.example",
            To = "Me <me@example.org>",
            ConversationTopic = "Budget",
            Body = "BODYTEXT-123",
            EventTime = DraftHarness.Now.AddMinutes(-5),
        });
        return h;
    }

    private static Exception[] Throwers() =>
        [new COMException("SECRET-MAIL-TEXT C:\\secret\\path", unchecked((int)0x80004005)), new InvalidOperationException("SECRET-MAIL-TEXT")];

    private static void AssertSafe(DraftHarness h, ManualDraftResult r)
    {
        Assert.False(r.Drafted);
        Assert.Equal(DraftTexts.ManualFailed(DraftTexts.ManualUnexpected), r.Message);
        Assert.DoesNotContain(h.AllText().Concat([r.Message]), s => s.Contains("SECRET-MAIL-TEXT") || s.Contains("secret"));
        Assert.Contains(h.Log.Lines, l => l.Contains("COMException") || l.Contains("InvalidOperationException"));
    }

    [Fact]
    public async Task Selected_throws()
    {
        foreach (var ex in Throwers())
        {
            using var h = Make();
            h.Mailbox.SelectedThrow = ex;
            AssertSafe(h, await h.Worker.DraftSelectedAsync(Ct));
        }
    }

    [Fact]
    public async Task Read_throws()
    {
        foreach (var ex in Throwers())
        {
            using var h = Make();
            h.Mailbox.ReadThrow = ex;
            AssertSafe(h, await h.Worker.DraftSelectedAsync(Ct));
        }
    }

    [Fact]
    public async Task Identity_throws()
    {
        foreach (var ex in Throwers())
        {
            using var h = Make();
            h.Mailbox.IdentityThrow = ex;
            AssertSafe(h, await h.Worker.DraftSelectedAsync(Ct));
            // The gate was released: another draft still works.
            h.Mailbox.IdentityThrow = null;
            h.Model.Enqueue(OkReply(0.9));
            Assert.True((await h.Worker.DraftSelectedAsync(Ct)).Drafted);
        }
    }

    [Fact]
    public async Task Model_throws()
    {
        foreach (var ex in Throwers())
        {
            using var h = Make();
            h.Model.OnRun = (_, _) => throw ex;
            AssertSafe(h, await h.Worker.DraftSelectedAsync(Ct));
        }
    }

    [Fact]
    public async Task Writer_throws()
    {
        foreach (var ex in Throwers())
        {
            using var h = Make();
            h.Model.Enqueue(OkReply(0.9));
            h.Writer.Behavior = (_, _) => throw ex;
            var r = await h.Worker.DraftSelectedAsync(Ct);
            Assert.False(r.Drafted);
            Assert.Equal(DraftTexts.ManualFailed("the draft couldn't be saved in Outlook"), r.Message);
            Assert.DoesNotContain(h.AllText(), s => s.Contains("SECRET-MAIL-TEXT"));
        }
    }

    [Fact]
    public async Task Cancellation_still_throws()
    {
        using var h = Make();
        using var cts = new CancellationTokenSource();
        h.Model.OnRun = (_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Worker.DraftSelectedAsync(cts.Token));

        using var h2 = Make();
        using var cts2 = new CancellationTokenSource();
        h2.Mailbox.SelectedThrow = new OperationCanceledException(cts2.Token);
        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h2.Worker.DraftSelectedAsync(cts2.Token));
    }

    [Fact]
    public async Task Manual_draft_does_not_wait_for_style_generation()
    {
        using var h = Make();
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Style.Block = block;
        h.Model.Enqueue(OkReply(0.9));

        var pass = h.Worker.RunPassAsync(Ct);
        Assert.Equal(1, h.Style.BeforeCalls);
        Assert.False(pass.IsCompleted);

        var r = await h.Worker.DraftSelectedAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.True(r.Drafted);
        Assert.False(pass.IsCompleted);

        block.SetResult();
        Assert.Equal(DraftPassEnd.Completed, (await pass.WaitAsync(TimeSpan.FromSeconds(30), Ct)).End);
    }
}
