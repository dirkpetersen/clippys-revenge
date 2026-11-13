namespace ClippysRevenge.Core.Tests;

public class SyncModelsTests
{
    [Theory]
    [InlineData("inbox", "Inbox")]
    [InlineData(" INBOX/Projects/ ", "Inbox\\Projects")]
    [InlineData("sent items", "Sent Items")]
    [InlineData("Sent Items\\2025", "Sent Items\\2025")]
    [InlineData("Archive\\\\x", "Archive\\x")]
    [InlineData("Sent", "Sent")]
    public void Canonical_Normalizes(string input, string expected) =>
        Assert.Equal(expected, FolderPaths.Canonical(input));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\\")]
    public void Canonical_EmptyThrows(string input) =>
        Assert.Throws<ArgumentException>(() => FolderPaths.Canonical(input));

    [Theory]
    [InlineData("Sent Items", true)]
    [InlineData("Sent Items\\X", true)]
    [InlineData("Inbox", false)]
    [InlineData("Sent", false)]
    public void IsUnderSentItems(string path, bool expected) =>
        Assert.Equal(expected, FolderPaths.IsUnderSentItems(path));

    [Fact]
    public void Labels()
    {
        Assert.Equal("", SyncLabels.MailboxPrefix(""));
        Assert.Equal("helpdesk@…", SyncLabels.MailboxPrefix("helpdesk@x.edu"));
        Assert.Equal("your mailbox", SyncLabels.MailboxName(""));
        Assert.Equal("a@x.edu", SyncLabels.MailboxName("a@x.edu"));
        Assert.Equal("Sent", SyncLabels.FolderLabel("Sent Items"));
        Assert.Equal("Inbox\\P", SyncLabels.FolderLabel("Inbox\\P"));
    }

    [Fact]
    public void Planner_Defaults()
    {
        var plans = SyncPlanner.Build(new Settings());
        var p = Assert.Single(plans);
        Assert.Equal("", p.Mailbox);
        Assert.Equal(["Inbox", "Sent Items"], p.Folders);
        Assert.Equal(12, p.RetentionMonths);
    }

    [Fact]
    public void Planner_SharedMailboxes()
    {
        var s = new Settings
        {
            SharedMailboxes =
            [
                new SharedMailbox { Smtp = "A@x.edu", Enabled = true, RetentionMonths = 3, IndexedFolders = ["inbox", "Inbox"] },
                new SharedMailbox { Smtp = "b@x.edu", Enabled = false },
                new SharedMailbox { Smtp = " C@X.EDU ", Enabled = true },
                new SharedMailbox { Smtp = "c@x.edu", Enabled = true, RetentionMonths = 1 },
                new SharedMailbox { Smtp = "", Enabled = true },
            ],
        };
        var plans = SyncPlanner.Build(s);
        Assert.Equal(["", "a@x.edu", "c@x.edu"], plans.Select(p => p.Mailbox));
        Assert.Equal(["Inbox"], plans[1].Folders);
        Assert.Equal(3, plans[1].RetentionMonths);
        Assert.Equal(["Inbox", "Sent Items"], plans[2].Folders);
        Assert.Equal(12, plans[2].RetentionMonths);
    }
}
