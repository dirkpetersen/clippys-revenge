namespace ClippysRevenge.Core.Tests;

public class EmailIndexRescanTests
{
    private static DateTimeOffset Now => DraftHarness.Now;

    [Fact]
    public void InboxSince_lists_inbox_rows_oldest_first_whatever_their_decision()
    {
        using var h = new DraftHarness();
        h.Seed("A", at: Now - TimeSpan.FromHours(24));
        h.Seed("B", at: Now - TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        h.Seed("C", at: Now - TimeSpan.FromHours(1));
        h.Seed("D", folder: "inbox", at: Now - TimeSpan.FromHours(2));
        h.Seed("E", folder: "Inbox\\Sub", at: Now - TimeSpan.FromHours(3));
        h.Seed("F", folder: "Sent Items", at: Now - TimeSpan.FromHours(1));
        var idx = h.Indexes.For("");
        idx.UpsertDecision(new AiDecision("C", null, null, Now, AiOutcome.Drafted, null, 1, 0, 0.9, "D1", Now));
        idx.UpsertDecision(new AiDecision("D", null, null, Now, AiOutcome.NoReply, null, 1, 0, 0.9));

        var rows = idx.InboxSince(Now - TimeSpan.FromHours(24), 500);

        Assert.Equal(["A", "D", "C"], rows.Select(r => r.EntryId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void InboxSince_rejects_a_bad_max(int max)
    {
        using var h = new DraftHarness();
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Indexes.For("").InboxSince(Now, max));
    }

    [Fact]
    public void InboxSince_max_keeps_the_oldest_rows()
    {
        using var h = new DraftHarness();
        h.Seed("A", at: Now - TimeSpan.FromHours(3));
        h.Seed("B", at: Now - TimeSpan.FromHours(2));
        h.Seed("C", at: Now - TimeSpan.FromHours(1));

        var rows = h.Indexes.For("").InboxSince(Now - TimeSpan.FromHours(24), 2);

        Assert.Equal(["A", "B"], rows.Select(r => r.EntryId));
    }
}
