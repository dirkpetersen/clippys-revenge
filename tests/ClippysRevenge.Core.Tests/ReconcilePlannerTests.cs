namespace ClippysRevenge.Core.Tests;

public class ReconcilePlannerTests
{
    private static readonly DateTimeOffset Cutoff = Retention.Cutoff(ManualTimeProvider.T0, 12);

    private static OutlookEntry O(string id, bool unread = false) => new(id, unread);
    private static IndexedEntry I(string id, bool unread = false, double days = 10) =>
        new(id, unread, Cutoff.AddDays(days));

    private static ReconcilePlan Plan(OutlookEntry[] o, IndexedEntry[] i) => ReconcilePlanner.Plan(o, i, Cutoff);

    [Fact]
    public void Identical_sets_are_empty()
    {
        var p = Plan([O("a"), O("b")], [I("a"), I("b")]);
        Assert.Empty(p.ToAdd);
        Assert.Empty(p.ToDelete);
        Assert.Empty(p.UnreadChanges);
        Assert.False(p.DeletesSuppressed);
    }

    [Fact]
    public void Missing_in_index_is_added() =>
        Assert.Equal(["c"], Plan([O("a"), O("b"), O("c")], [I("a"), I("b")]).ToAdd);

    [Fact]
    public void Gone_from_outlook_is_deleted() =>
        Assert.Equal(["b"], Plan([O("a")], [I("a"), I("b", days: 2)]).ToDelete);

    [Fact]
    public void Inside_edge_margin_is_kept() =>
        Assert.Empty(Plan([O("a")], [I("a"), I("b", days: 0.5)]).ToDelete);

    [Fact]
    public void Older_than_cutoff_is_kept() =>
        Assert.Empty(Plan([O("a")], [I("a"), I("b", days: -30)]).ToDelete);

    [Fact]
    public void Unread_changes_both_ways()
    {
        Assert.Equal([("a", true)], Plan([O("a", true)], [I("a", false)]).UnreadChanges);
        Assert.Equal([("a", false)], Plan([O("a", false)], [I("a", true)]).UnreadChanges);
    }

    [Fact]
    public void Ids_match_case_insensitively_and_use_stored_id()
    {
        var p = Plan([O("ABC", true)], [I("abc")]);
        Assert.Empty(p.ToAdd);
        Assert.Empty(p.ToDelete);
        Assert.Equal([("abc", true)], p.UnreadChanges);
    }

    [Fact]
    public void Outlook_duplicates_collapse_first_wins()
    {
        var p = Plan([O("a", true), O("A", false)], [I("a", false)]);
        Assert.Equal([("a", true)], p.UnreadChanges);
        Assert.Empty(p.ToAdd);
    }

    [Fact]
    public void Blank_ids_ignored() => Assert.Empty(Plan([O(""), O(" ")], []).ToAdd);

    [Fact]
    public void Empty_outlook_with_20_candidates_is_suppressed()
    {
        var idx = Enumerable.Range(0, 20).Select(n => I("x" + n)).ToArray();
        var p = Plan([], idx);
        Assert.Empty(p.ToDelete);
        Assert.True(p.DeletesSuppressed);
        Assert.Equal(20, p.SuppressedCount);
        Assert.Empty(p.ToAdd);
    }

    [Fact]
    public void Empty_outlook_with_19_candidates_deletes()
    {
        var idx = Enumerable.Range(0, 19).Select(n => I("x" + n)).ToArray();
        var p = Plan([], idx);
        Assert.Equal(19, p.ToDelete.Count);
        Assert.False(p.DeletesSuppressed);
    }

    [Fact]
    public void Empty_outlook_with_old_rows_has_no_candidates()
    {
        var idx = Enumerable.Range(0, 25).Select(n => I("x" + n, days: 0.5)).ToArray();
        var p = Plan([], idx);
        Assert.Empty(p.ToDelete);
        Assert.False(p.DeletesSuppressed);
    }

    [Fact]
    public void ToAdd_keeps_outlook_order() =>
        Assert.Equal(["z", "a", "m"], Plan([O("z"), O("a"), O("m")], []).ToAdd);

    [Fact]
    public void Null_lists_throw()
    {
        Assert.Throws<ArgumentNullException>(() => ReconcilePlanner.Plan(null!, [], Cutoff));
        Assert.Throws<ArgumentNullException>(() => ReconcilePlanner.Plan([], null!, Cutoff));
    }

    [Fact]
    public void Disjoint_sets_suppress_deletes()
    {
        var idx = Enumerable.Range(0, 3000).Select(n => I("old" + n)).ToArray();
        var o = Enumerable.Range(0, 3000).Select(n => O("new" + n)).ToArray();
        var p = Plan(o, idx);
        Assert.Empty(p.ToDelete);
        Assert.True(p.DeletesSuppressed);
        Assert.True(p.IdMismatch);
        Assert.Equal(3000, p.SuppressedCount);
    }

    [Fact]
    public void Small_deletion_with_overlap_still_deletes()
    {
        var idx = Enumerable.Range(0, 100).Select(n => I("m" + n)).ToArray();
        var o = idx.Take(90).Select(r => O(r.EntryId)).ToArray();
        var p = Plan(o, idx);
        Assert.Equal(10, p.ToDelete.Count);
        Assert.False(p.DeletesSuppressed);
    }

    [Fact]
    public void Over_half_missing_suppresses_deletes()
    {
        var idx = Enumerable.Range(0, 100).Select(n => I("m" + n)).ToArray();
        var o = idx.Take(40).Select(r => O(r.EntryId)).ToArray();
        var p = Plan(o, idx);
        Assert.Empty(p.ToDelete);
        Assert.True(p.DeletesSuppressed);
        Assert.Equal(60, p.SuppressedCount);
    }

    [Fact]
    public void Many_deletions_below_half_still_delete()
    {
        var idx = Enumerable.Range(0, 100).Select(n => I("m" + n)).ToArray();
        var o = idx.Take(70).Select(r => O(r.EntryId)).ToArray();
        Assert.Equal(30, Plan(o, idx).ToDelete.Count);
    }
}
