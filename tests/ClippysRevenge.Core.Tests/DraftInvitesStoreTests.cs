using Microsoft.Extensions.Logging;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public sealed class DraftInvitesStoreTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-di-store-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLog _log = new();

    private string FilePath => Path.Combine(_dir, DraftInvitesStore.FileName);

    private DraftInvitesStore NewStore()
    {
        Directory.CreateDirectory(_dir);
        return new DraftInvitesStore(FilePath, _log);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static DraftInviteEntry E(string id, int minutes = 0, string mailbox = "") =>
        new(id, "S-" + id, mailbox, Base.AddMinutes(minutes));

    [Fact]
    public void Missing_file_is_empty_without_log()
    {
        Assert.Empty(NewStore().Load());
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void Round_trip()
    {
        var s = NewStore();
        var a = new DraftInviteEntry("AB12", "ST1", "", Base.AddTicks(1234567));
        var b = new DraftInviteEntry("CD34", "ST2", "help@x.edu", Base.AddMinutes(5));
        s.Save([a, b]);
        Assert.Equal([a, b], s.Load());
        var text = File.ReadAllText(FilePath);
        foreach (var k in new[] { "\"entryId\"", "\"storeId\"", "\"mailbox\"", "\"createdUtc\"" }) Assert.Contains(k, text);
        foreach (var k in new[] { "subject", "start", "end" }) Assert.DoesNotContain(k, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_is_atomic()
    {
        var s = new DraftInvitesStore(Path.Combine(_dir, "sub", DraftInvitesStore.FileName), _log);
        s.Save([E("A")]);
        Assert.True(File.Exists(s.Path));
        Assert.False(File.Exists(s.Path + ".tmp"));
    }

    [Fact]
    public void Cap_200_keeps_newest()
    {
        var s = NewStore();
        var all = Enumerable.Range(0, 201).Select(i => E("E" + i, i)).ToList();
        s.Save(all);
        var loaded = s.Load();
        Assert.Equal(200, loaded.Count);
        Assert.DoesNotContain(loaded, e => e.EntryId == "E0");

        // Hand-written file with 201 entries.
        var json = "[" + string.Join(",", all.Select(e => $"{{\"entryId\":\"{e.EntryId}\",\"storeId\":\"\",\"mailbox\":\"\",\"createdUtc\":\"{e.CreatedUtc:O}\"}}")) + "]";
        File.WriteAllText(FilePath, json);
        var again = s.Load();
        Assert.Equal(200, again.Count);
        Assert.DoesNotContain(again, e => e.EntryId == "E0");
    }

    [Fact]
    public void Sorted_by_created()
    {
        var s = NewStore();
        s.Save([E("C", 3), E("A", 1), E("B", 2)]);
        Assert.Equal(["A", "B", "C"], s.Load().Select(e => e.EntryId));
    }

    [Fact]
    public void Malformed_json()
    {
        var s = NewStore();
        File.WriteAllText(FilePath, "{\"x\":");
        Assert.Empty(s.Load());
        Assert.Single(_log.Lines, l => l.Contains("draft-invites.json could not be read"));
        Assert.Equal("{\"x\":", File.ReadAllText(FilePath));
        var e = E("A");
        s.Save([e]);
        Assert.Equal([e], s.Load());
    }

    [Fact]
    public void Not_an_array()
    {
        var s = NewStore();
        File.WriteAllText(FilePath, "{}");
        Assert.Empty(s.Load());
        Assert.Contains(_log.Lines, l => l.Contains("NotAnArray"));
    }

    [Fact]
    public void Too_large()
    {
        var s = NewStore();
        File.WriteAllBytes(FilePath, new byte[DraftInvitesStore.MaxFileBytes + 1]);
        Assert.Empty(s.Load());
        Assert.Contains(_log.Lines, l => l.Contains("TooLarge"));
    }

    [Fact]
    public void Invalid_entries_skipped()
    {
        var s = NewStore();
        var longId = new string('a', DraftInvitesStore.MaxEntryIdLength + 1);
        var longStore = new string('b', DraftInvitesStore.MaxStoreIdLength + 1);
        File.WriteAllText(FilePath, "[null,{},{\"entryId\":\" \"}," +
            $"{{\"entryId\":\"{longId}\",\"createdUtc\":\"2026-11-14T10:00:00+00:00\"}}," +
            $"{{\"entryId\":\"X1\",\"storeId\":\"{longStore}\",\"createdUtc\":\"2026-11-14T10:00:00+00:00\"}}," +
            "{\"entryId\":\"X2\",\"storeId\":\"s\"}," +
            "{\"entryId\":\"OK\",\"storeId\":\"s\",\"mailbox\":\"\",\"createdUtc\":\"2026-11-14T10:00:00+00:00\"}]");
        var loaded = s.Load();
        Assert.Equal("OK", Assert.Single(loaded).EntryId);
    }

    [Fact]
    public void Duplicates_keep_first()
    {
        var s = NewStore();
        File.WriteAllText(FilePath,
            "[{\"entryId\":\"AB12\",\"storeId\":\"first\",\"createdUtc\":\"2026-11-14T10:00:00+00:00\"}," +
            "{\"entryId\":\"ab12\",\"storeId\":\"second\",\"createdUtc\":\"2026-11-14T11:00:00+00:00\"}]");
        var e = Assert.Single(s.Load());
        Assert.Equal("first", e.StoreId);
    }

    [Fact]
    public void Mailbox_normalized()
    {
        var s = NewStore();
        File.WriteAllText(FilePath,
            "[{\"entryId\":\"A\",\"mailbox\":\" Help@X.edu \",\"createdUtc\":\"2026-11-14T10:00:00+00:00\"}," +
            "{\"entryId\":\"B\",\"mailbox\":null,\"createdUtc\":\"2026-11-14T11:00:00+00:00\"}]");
        var loaded = s.Load();
        Assert.Equal("help@x.edu", loaded[0].Mailbox);
        Assert.Equal("", loaded[1].Mailbox);
    }

    [Fact]
    public void Unknown_properties_ignored()
    {
        var s = NewStore();
        File.WriteAllText(FilePath, "[{\"entryId\":\"A\",\"subject\":\"x\",\"createdUtc\":\"2026-11-14T10:00:00+00:00\"}]");
        var loaded = s.Load();
        Assert.Single(loaded);
        s.Save(loaded);
        Assert.DoesNotContain("subject", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Blank_path_rejected() => Assert.Throws<ArgumentException>(() => new DraftInvitesStore(" "));
}
