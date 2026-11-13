using System.Text.RegularExpressions;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests;

public class AuthTokenTests
{
    [Fact]
    public void Generate_Is64LowerHex() =>
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), AuthToken.Generate());

    [Fact]
    public void Generate_IsDistinct() =>
        Assert.Equal(100, Enumerable.Range(0, 100).Select(_ => AuthToken.Generate()).Distinct().Count());
}
