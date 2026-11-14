using Xunit;

namespace ClippysRevenge.Core.Tests;

public class DraftInviteCategoriesTests
{
    private const string Name = "Clippy draft invite";

    [Theory]
    [InlineData("Clippy draft invite", ",", "")]
    [InlineData("Red, Clippy draft invite, Blue", ",", "Red, Blue")]
    [InlineData("C, B, Clippy draft invite, A", ",", "C, B, A")]
    [InlineData("Red;clippy DRAFT invite;Blue", ";", "Red; Blue")]
    [InlineData("Red; Clippy draft invite", ",", "Red")]
    [InlineData("  Clippy draft invite  ,Red", ",", "Red")]
    [InlineData("Clippy draft invite, Red, Clippy draft invite", ",", "Red")]
    [InlineData("Red,,Clippy draft invite, ,Blue", ",", "Red, Blue")]
    [InlineData("A, Clippy draft invite; B", ",", "A, B")]
    [InlineData("A; Clippy draft invite, B", ",", "A; B")]
    [InlineData("Red|Clippy draft invite|Blue", "|", "Red| Blue")]
    public void Removes_our_category(string input, string sep, string expected)
    {
        Assert.True(DraftInviteCategories.TryRemove(input, Name, sep, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Red, Blue", ",")]
    [InlineData("Clippy draft invites, My Clippy draft invite", ",")]
    [InlineData("", ",")]
    [InlineData("Red|Clippy draft invite|Blue", null)]
    public void Leaves_other_values_alone(string input, string? sep)
    {
        Assert.False(DraftInviteCategories.TryRemove(input, Name, sep, out var result));
        Assert.Equal(input, result);
    }

    [Fact]
    public void Null_input()
    {
        Assert.False(DraftInviteCategories.TryRemove(null, Name, ",", out var result));
        Assert.Equal("", result);
    }

    [Fact]
    public void Split_handles_separators()
    {
        Assert.Equal(["a", "b", "c"], DraftInviteCategories.Split("a; b,c", null));
        Assert.Empty(DraftInviteCategories.Split(" , ;", ","));
        Assert.Empty(DraftInviteCategories.Split(null, ","));
    }
}
