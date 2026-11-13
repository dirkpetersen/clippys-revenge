using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests;

public sealed class SharedMailboxMenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-sm-" + Guid.NewGuid().ToString("N"));

    public SharedMailboxMenuTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static MailboxStore Own() => new("Me", "s0", "me@x.edu", true);
    private static MailboxStore Box(string name, string smtp) => new(name, "id-" + smtp, smtp, false);

    [Fact]
    public void MountedUnconfiguredShowsUnchecked()
    {
        var m = SharedMailboxMenu.Build([Own(), Box("Helpdesk", "helpdesk@x.edu")], []);
        var e = Assert.Single(m.Entries);
        Assert.False(e.Checked);
        Assert.False(e.NotInOutlook);
        Assert.Equal("Helpdesk <helpdesk@x.edu>", e.Label);
        Assert.False(m.StartOutlookHint);
    }

    [Fact]
    public void OwnMailboxIsExcluded()
    {
        var m = SharedMailboxMenu.Build([Own()], []);
        Assert.Empty(m.Entries);
    }

    [Fact]
    public void EnabledConfiguredIsChecked_DisabledIsNot()
    {
        var cfg = new[]
        {
            new SharedMailbox { Smtp = "a@x.edu", DisplayName = "A", Enabled = true },
            new SharedMailbox { Smtp = "b@x.edu", DisplayName = "B", Enabled = false },
        };
        var m = SharedMailboxMenu.Build([Own(), Box("A", "a@x.edu"), Box("B", "b@x.edu")], cfg);
        Assert.Equal(["a@x.edu", "b@x.edu"], m.Entries.Select(e => e.Smtp));
        Assert.True(m.Entries[0].Checked);
        Assert.False(m.Entries[1].Checked);
    }

    [Fact]
    public void ConfiguredButNotMountedIsMarked()
    {
        var cfg = new[] { new SharedMailbox { Smtp = "Gone@X.edu", DisplayName = "Gone", Enabled = true } };
        var m = SharedMailboxMenu.Build([Own()], cfg);
        var e = Assert.Single(m.Entries);
        Assert.True(e.NotInOutlook);
        Assert.True(e.Checked);
        Assert.Equal("gone@x.edu", e.Smtp);
        Assert.EndsWith("(not in Outlook)", e.Label);
    }

    [Fact]
    public void OutlookNotRunningShowsConfiguredOnlyAndHint()
    {
        var cfg = new[] { new SharedMailbox { Smtp = "a@x.edu", Enabled = true } };
        var m = SharedMailboxMenu.Build(null, cfg);
        var e = Assert.Single(m.Entries);
        Assert.False(e.NotInOutlook);
        Assert.Equal("a@x.edu", e.Label);
        Assert.True(m.StartOutlookHint);
    }

    [Fact]
    public void ConfiguredFirstThenMountedAndNoDuplicates()
    {
        var cfg = new[] { new SharedMailbox { Smtp = "b@x.edu", DisplayName = "B" }, new SharedMailbox { Smtp = "B@x.edu" } };
        var m = SharedMailboxMenu.Build([Box("A", "a@x.edu"), Box("B", "b@x.edu")], cfg);
        Assert.Equal(["b@x.edu", "a@x.edu"], m.Entries.Select(e => e.Smtp));
    }

    [Fact]
    public void StoreWithUnknownSmtpIsSkipped()
    {
        var m = SharedMailboxMenu.Build([Box("Mystery", "")], []);
        Assert.Empty(m.Entries);
    }

    private (SharedMailboxToggler t, Settings s, MailboxIndexes ix, int[] calls) Toggler()
    {
        var s = Settings.CreateDefault(_dir);
        var ix = new MailboxIndexes(_dir, []);
        var calls = new int[2];
        return (new SharedMailboxToggler(s, ix, _ => calls[0]++, () => calls[1]++), s, ix, calls);
    }

    [Fact]
    public void CheckAddsEntryOpensIndexSavesAndNotifies()
    {
        var (t, s, ix, calls) = Toggler();
        using (ix)
        {
            t.Check("Help@X.edu", "Help");
            var e = Assert.Single(s.SharedMailboxes);
            Assert.True(e.Enabled);
            Assert.Equal("help@x.edu", e.Smtp);
            Assert.Contains("help@x.edu", ix.Mailboxes);
            Assert.Equal([1, 1], calls);
        }
    }

    [Fact]
    public void CheckInvalidAddressChangesNothing()
    {
        var (t, s, ix, calls) = Toggler();
        using (ix)
        {
            Assert.ThrowsAny<Exception>(() => t.Check("not an address", "x"));
            Assert.Empty(s.SharedMailboxes);
            Assert.Equal([0, 0], calls);
        }
    }

    [Fact]
    public void UncheckKeepIndexKeepsFileAndEntry()
    {
        var (t, s, ix, _) = Toggler();
        using (ix)
        {
            t.Check("a@x.edu", "A");
            var path = MailboxIndexes.PathFor(_dir, "a@x.edu");
            Assert.True(File.Exists(path));
            t.Uncheck("a@x.edu", deleteIndex: false);
            Assert.False(Assert.Single(s.SharedMailboxes).Enabled);
            Assert.True(File.Exists(path));
            Assert.DoesNotContain("a@x.edu", ix.Mailboxes);
        }
    }

    [Fact]
    public void UncheckDeleteRemovesFile()
    {
        var (t, s, ix, _) = Toggler();
        using (ix)
        {
            t.Check("a@x.edu", "A");
            t.Uncheck("a@x.edu", deleteIndex: true);
            Assert.False(File.Exists(MailboxIndexes.PathFor(_dir, "a@x.edu")));
            Assert.False(s.SharedMailboxes[0].Enabled);
        }
    }

    [Fact]
    public void RecheckReenablesSameEntry()
    {
        var (t, s, ix, _) = Toggler();
        using (ix)
        {
            t.Check("a@x.edu", "A");
            s.SharedMailboxes[0].RetentionMonths = 3;
            t.Uncheck("a@x.edu", false);
            t.Check("a@x.edu", "A");
            var e = Assert.Single(s.SharedMailboxes);
            Assert.True(e.Enabled);
            Assert.Equal(3, e.RetentionMonths);
        }
    }
}
