using ClippysRevenge.Core;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class AttachmentBudgetTests
{
    private static int?[] Alloc(params (int Tier, int Len)[] items) =>
        AttachmentBudget.Allocate(items.Select(i => new BudgetItem(i.Tier, i.Len)).ToList()).ToArray();

    [Fact]
    public void All_fit() => Assert.Equal<int?[]>([10000, 10000, 10000], Alloc((1, 10000), (2, 10000), (3, 10000)));

    [Fact]
    public void Water_fill_in_tier_1()
    {
        Assert.Equal<int?[]>([20000, 20000], Alloc((1, 30000), (1, 30000)));
        Assert.Equal<int?[]>([5000, 35000], Alloc((1, 5000), (1, 50000)));
    }

    [Fact]
    public void Exact_level() =>
        Assert.Equal<int?[]>([10000, 15000, 15000], Alloc((1, 10000), (1, 20000), (1, 20001)));

    [Fact]
    public void Floor_skips_the_rest()
    {
        var r = Alloc(Enumerable.Repeat((1, 5000), 25).ToArray());
        Assert.All(r.Take(20), x => Assert.Equal(2000, x));
        Assert.All(r.Skip(20), x => Assert.Null(x));
    }

    [Fact]
    public void Floor_with_short_items()
    {
        var r = Alloc([(1, 1500), .. Enumerable.Repeat((1, 5000), 30)]);
        Assert.Equal(1500, r[0]);
        Assert.All(r.Skip(1).Take(19), x => Assert.Equal(2000, x));
        Assert.All(r.Skip(20), x => Assert.Null(x));
    }

    [Fact]
    public void Later_tiers_are_starved_unless_they_fit()
    {
        Assert.Equal<int?[]>([39000, null], Alloc((1, 39000), (2, 5000)));
        Assert.Equal<int?[]>([39000, 800], Alloc((1, 39000), (2, 800)));
    }

    [Fact]
    public void Tier_2_shares_what_tier_1_left()
    {
        Assert.Equal<int?[]>([20000, 10000, 10000, null], Alloc((1, 20000), (2, 15000), (2, 15000), (3, 100)));
    }

    [Fact]
    public void Input_order_is_kept_with_interleaved_tiers() =>
        Assert.Equal<int?[]>([5000, 30000, 5000], Alloc((3, 10000), (1, 30000), (3, 20000)));

    [Fact]
    public void Zero_length_keeps_zero() => Assert.Equal<int?[]>([0, 0], Alloc((1, 0), (3, 0)));

    [Fact]
    public void Invalid_input_throws()
    {
        Assert.Throws<ArgumentNullException>(() => AttachmentBudget.Allocate(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => Alloc((1, -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Alloc((0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Alloc((4, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AttachmentBudget.Allocate([], -1));
    }

    [Fact]
    public void Cut_behaves()
    {
        Assert.Equal("abc…", AttachmentBudget.Cut("abcdef", 4));
        Assert.Equal("abc", AttachmentBudget.Cut("abc", 10));
        Assert.Equal("", AttachmentBudget.Cut("abc", 0));
        var pair = "ab\U0001F600cd";                 // a b <high><low> c d
        var cut = AttachmentBudget.Cut(pair, 4);      // would keep "ab<high>" + ellipsis
        Assert.Equal("ab…", cut);
        Assert.True(cut.Length <= 4);
    }
}
