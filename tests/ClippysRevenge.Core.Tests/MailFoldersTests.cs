using ClippysRevenge.Core;
using ModelContextProtocol;

namespace ClippysRevenge.Core.Tests;

public class MailFoldersTests
{
    [Theory]
    [InlineData("inbox", "Inbox", FolderRoot.Inbox, "")]
    [InlineData(" Sent items ", "Sent Items", FolderRoot.SentItems, "")]
    [InlineData("Inbox/Projects", "Inbox\\Projects", FolderRoot.Inbox, "Projects")]
    [InlineData("INBOX\\ Projects \\", "Inbox\\Projects", FolderRoot.Inbox, "Projects")]
    [InlineData("\\Inbox", "Inbox", FolderRoot.Inbox, "")]
    [InlineData("Archive\\2025", "Archive\\2025", FolderRoot.StoreRoot, "Archive|2025")]
    [InlineData("Inboxes", "Inboxes", FolderRoot.StoreRoot, "Inboxes")]
    [InlineData("Sent Items\\Old", "Sent Items\\Old", FolderRoot.SentItems, "Old")]
    public void Parse_cases(string input, string canonical, FolderRoot root, string sub)
    {
        var p = MailFolderPath.Parse(input);
        Assert.Equal(canonical, p.Canonical);
        Assert.Equal(canonical, MailFolderPath.Canonicalize(input));
        Assert.Equal(root, p.Root);
        Assert.Equal(sub.Length == 0 ? [] : sub.Split('|'), p.SubPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\\")]
    [InlineData("/ /")]
    public void Parse_empty_throws(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => MailFolderPath.Parse(input));
        Assert.StartsWith("Folder path is empty.", ex.Message);
        Assert.Throws<ArgumentNullException>(() => MailFolderPath.Parse(null!));
    }

    [Theory]
    [InlineData("Inbox", EventTimeField.Received)]
    [InlineData("Sent Items", EventTimeField.Sent)]
    [InlineData("Sent Items\\Old", EventTimeField.Sent)]
    [InlineData("Archive", EventTimeField.Received)]
    [InlineData("Inbox\\Sent Items", EventTimeField.Received)]
    public void TimeFieldFor(string path, EventTimeField expected) =>
        Assert.Equal(expected, MailFolderPath.TimeFieldFor(MailFolderPath.Parse(path)));

    [Fact]
    public void IndexedFoldersFor_cases()
    {
        var s = new Settings();
        Assert.Equal(["Inbox", "Sent Items"], MailFolderPath.IndexedFoldersFor(s, ""));

        s.IndexedFolders = ["inbox", "Inbox", "Inbox/Sub", " ", "\\"];
        Assert.Equal(["Inbox", "Inbox\\Sub"], MailFolderPath.IndexedFoldersFor(s, null));

        var sm = s.AddOrEnableSharedMailbox("Team@X.org", null);
        sm.IndexedFolders = ["Inbox"];
        Assert.Equal(["Inbox"], MailFolderPath.IndexedFoldersFor(s, " TEAM@x.org "));
        Assert.Empty(MailFolderPath.IndexedFoldersFor(s, "nobody@x.org"));

        sm.IndexedFolders = null!;
        Assert.Empty(MailFolderPath.IndexedFoldersFor(s, "team@x.org"));

        Assert.Throws<ArgumentNullException>(() => MailFolderPath.IndexedFoldersFor(null!, ""));
    }

    [Fact]
    public void FromOutlook_cases()
    {
        Assert.Equal("Inbox\\Projekte",
            MailFolderPath.FromOutlook("\\\\Mailbox - Bob\\Posteingang\\Projekte", "Posteingang", "Gesendete Elemente"));
        Assert.Equal("Sent Items",
            MailFolderPath.FromOutlook("\\\\team@x.org\\Gesendete Elemente", "Posteingang", "Gesendete Elemente"));
        Assert.Equal("Archive\\2025", MailFolderPath.FromOutlook("\\\\Bob\\Archive\\2025", "Inbox", "Sent Items"));
        Assert.Null(MailFolderPath.FromOutlook("\\\\Bob", "Inbox", "Sent Items"));
        Assert.Null(MailFolderPath.FromOutlook(null, "Inbox", "Sent Items"));
        Assert.Equal("Inbox", MailFolderPath.FromOutlook("\\\\Bob\\Inbox", null, null));
    }

    [Fact]
    public void FromOutlook_canonicalizes_localized_drafts_and_deleted_items()
    {
        Assert.Equal("Drafts",
            MailFolderPath.FromOutlook("\\\\Bob\\Entwürfe", "Posteingang", "Gesendete Elemente", "Entwürfe", "Gelöschte Elemente"));
        Assert.Equal("Deleted Items",
            MailFolderPath.FromOutlook("\\\\Bob\\Gelöschte Elemente", "Posteingang", "Gesendete Elemente", "Entwürfe", "Gelöschte Elemente"));
        Assert.Equal("Drafts\\Alt",
            MailFolderPath.FromOutlook("\\\\Bob\\entwürfe\\Alt", "Posteingang", "Gesendete Elemente", "Entwürfe", "Gelöschte Elemente"));
        Assert.Equal("Deleted Items\\2025",
            MailFolderPath.FromOutlook("\\\\Bob\\Gelöschte Elemente\\2025", "Posteingang", "Gesendete Elemente", "Entwürfe", "Gelöschte Elemente"));
        // Other folders untouched; a nested folder with the localized name is not the default folder.
        Assert.Equal("Archive\\Entwürfe",
            MailFolderPath.FromOutlook("\\\\Bob\\Archive\\Entwürfe", "Posteingang", "Gesendete Elemente", "Entwürfe", "Gelöschte Elemente"));
        Assert.Equal("Entwürfe",
            MailFolderPath.FromOutlook("\\\\Bob\\Entwürfe", "Inbox", "Sent Items"));
        Assert.Equal("Drafts", MailFolderPath.FromOutlook("\\\\Bob\\Drafts", "Inbox", "Sent Items", "Drafts", "Deleted Items"));
    }

    [Fact]
    public void Parse_drafts_and_deleted_items_roots_and_round_trip()
    {
        var d = MailFolderPath.Parse(" drafts/Old ");
        Assert.Equal(FolderRoot.Drafts, d.Root);
        Assert.Equal("Drafts\\Old", d.Canonical);
        Assert.Equal(["Old"], d.SubPath);
        var x = MailFolderPath.Parse("DELETED ITEMS");
        Assert.Equal(FolderRoot.DeletedItems, x.Root);
        Assert.Equal("Deleted Items", x.Canonical);
        Assert.Empty(x.SubPath);
        Assert.Equal(EventTimeField.Received, MailFolderPath.TimeFieldFor(x));
        Assert.Equal(x.Canonical, MailFolderPath.Canonicalize(x.Canonical));
        Assert.Equal(FolderRoot.StoreRoot, MailFolderPath.Parse("Draftsx").Root);
        var fromOutlook = MailFolderPath.FromOutlook("\\\\Bob\\Entwürfe\\Alt", "Inbox", "Sent Items", "Entwürfe", "Gelöschte Elemente");
        Assert.Equal(fromOutlook, MailFolderPath.Parse(fromOutlook!).Canonical);
    }
    [Fact]
    public void NotFoundException_messages()
    {
        var own = new MailFolderNotFoundException("", "Inbox\\Gone");
        Assert.Equal("Folder \"Inbox\\Gone\" was not found in your own mailbox. Check the indexed folders in the Clippy tray settings.", own.Message);
        var shared = new MailFolderNotFoundException("team@x.org", "Archive");
        Assert.Equal("Folder \"Archive\" was not found in mailbox \"team@x.org\". Check the indexed folders in the Clippy tray settings.", shared.Message);
        Assert.IsAssignableFrom<McpException>(shared);
        Assert.Equal("team@x.org", shared.Mailbox);
        Assert.Equal("Archive", shared.Path);
    }
}
