namespace ClippysRevenge.Core.Tests;

public class ModelAliasesTests
{
    [Theory]
    [InlineData("opus", true)]
    [InlineData("sonnet", true)]
    [InlineData("haiku", true)]
    [InlineData("Opus", false)]
    [InlineData(" opus", false)]
    [InlineData("opus ", false)]
    [InlineData("fable", false)]
    [InlineData("claude-opus-5-5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAlias(string? model, bool expected) => Assert.Equal(expected, ModelAliases.IsAlias(model));

    [Theory]
    [InlineData("opus", "claude-opus-5-5")]
    [InlineData("sonnet", "claude-sonnet-5-5")]
    [InlineData("haiku", "claude-haiku-5-5")]
    public void Fallback_PinnedIds(string alias, string id) => Assert.Equal(id, ModelAliases.Fallback(alias));

    [Theory]
    [InlineData("fable")]
    [InlineData("Opus")]
    public void Fallback_NotAnAlias_Throws(string alias)
    {
        var e = Assert.Throws<ArgumentException>(() => ModelAliases.Fallback(alias));
        Assert.Equal("alias", e.ParamName);
    }

    [Fact]
    public void FallbackIds_PassTheCliModelRule()
    {
        foreach (var alias in ModelAliases.All)
        {
            Assert.True(ClaudeCliArgs.IsValidModel(ModelAliases.Fallback(alias)));
            Assert.True(ClaudeCliArgs.IsValidModel(alias));
        }
    }
}
