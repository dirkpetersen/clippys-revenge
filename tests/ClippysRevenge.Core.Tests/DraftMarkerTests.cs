namespace ClippysRevenge.Core.Tests;

public class DraftMarkerTests
{
    [Fact]
    public void Constants()
    {
        Assert.Equal("ClippyDraft", DraftMarker.Name);
        Assert.Equal("http://schemas.microsoft.com/mapi/string/{00020329-0000-0000-C000-000000000046}/ClippyDraft", DraftMarker.PropertyName);
        Assert.EndsWith("/" + DraftMarker.Name, DraftMarker.PropertyName);
        Assert.Equal("ai", DraftMarker.Ai);
        Assert.Equal("tool", DraftMarker.Tool);
    }

    [Theory]
    [InlineData("ai", "ai")]
    [InlineData("AI", "ai")]
    [InlineData("tool", "tool")]
    [InlineData("Tool", "tool")]
    [InlineData("x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData(" ai", null)]
    [InlineData("ai ", null)]
    [InlineData("aitool", null)]
    public void Normalize(string? input, string? expected) =>
        Assert.Equal(expected, DraftMarker.Normalize(input));
}
