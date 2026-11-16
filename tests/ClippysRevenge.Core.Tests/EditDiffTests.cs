using ClippysRevenge.Core;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class EditDiffTests
{
    private const string Draft = "Hi Ann, thanks for the update. I will check and get back to you tomorrow.";

    private static string Words(int n, int changeLast = 0)
    {
        var w = Enumerable.Range(0, n).Select(i => "w" + i).ToArray();
        for (var k = 1; k <= changeLast; k++) w[^k] = "x" + k;
        return string.Join(' ', w);
    }

    [Fact]
    public void Tokens_words_and_punctuation()
    {
        Assert.Equal(["Hi", "Ann", ",", "thanks", "!", "See", "you"], EditDiff.Tokens("Hi Ann, thanks!  See you"));
    }

    [Fact]
    public void Tokens_apostrophes_digits_emoji_and_empty()
    {
        Assert.Equal(["don't", "it’s"], EditDiff.Tokens("don't it’s"));
        Assert.Equal(["Room", "4B", "at", "10", ":", "30"], EditDiff.Tokens("Room 4B at 10:30"));
        Assert.Equal(["Hi", "\U0001F44D"], EditDiff.Tokens("Hi \U0001F44D"));
        Assert.Empty(EditDiff.Tokens(null));
        Assert.Empty(EditDiff.Tokens(" \n\t "));
    }

    [Fact]
    public void Similarity_basics()
    {
        Assert.Equal(1.0, EditDiff.Similarity(Draft, Draft));
        Assert.Equal(1.0, EditDiff.Similarity("", ""));
        Assert.Equal(1.0, EditDiff.Similarity(null, null));
        Assert.Equal(0.0, EditDiff.Similarity("Hi", ""));
        Assert.Equal(0.0, EditDiff.Similarity(null, "Hi"));
        Assert.Equal(1.0, EditDiff.Similarity("Hi  Ann\n\nthanks", "Hi Ann thanks"));
        Assert.Equal(0.0, EditDiff.Similarity("hi", "Hi"));
    }

    [Fact]
    public void Small_edit_is_not_unchanged()
    {
        var sim = EditDiff.Similarity(Draft, Draft.Replace("tomorrow", "Friday"));
        Assert.Equal(34.0 / 36, sim);
        Assert.False(EditDiff.IsUnchanged(sim));
    }

    [Fact]
    public void Rewrite()
    {
        Assert.Equal(2.0 / 7, EditDiff.Similarity("Thanks, sounds good.", "No."));
    }

    [Fact]
    public void Threshold_cases()
    {
        var sim50 = EditDiff.Similarity(Words(50), Words(50, 1));
        Assert.Equal(0.98, sim50);
        Assert.True(EditDiff.IsUnchanged(sim50));
        var sim51 = EditDiff.Similarity(Words(51), Words(51, 1));
        Assert.Equal(100.0 / 102, sim51);
        Assert.True(EditDiff.IsUnchanged(sim51));
        var sim49 = EditDiff.Similarity(Words(49), Words(49, 1));
        Assert.Equal(96.0 / 98, sim49);
        Assert.False(EditDiff.IsUnchanged(sim49));
    }

    [Fact]
    public void Large_texts_complete()
    {
        var a = string.Concat(Enumerable.Range(0, 300).Select(i => (i % 10 == 0 ? "wxyz" : "abcd") + " "));
        var b = string.Concat(Enumerable.Range(0, 300).Select(i => (i % 10 == 0 ? "qrst" : "abcd") + " "));
        Assert.Equal(1500, a.Length);
        var sim = EditDiff.Similarity(a, b);
        Assert.InRange(sim, 0.01, 0.99);
        Assert.NotEqual(0.0, sim);
        Assert.NotEqual(1.0, sim);
    }
}
