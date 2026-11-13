using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests;

public class DraftBodyTests
{
    [Theory]
    [InlineData("<html><head><style>p{}</style></head><body lang=EN-US link=\"#0563C1\"><p>Hi</p></body></html>", "<p>Hi</p>")]
    [InlineData("<BODY>x</BODY >", "x")]
    [InlineData("<p>no body</p>", "<p>no body</p>")]
    [InlineData("<body>a", "a")]
    [InlineData("<table><tbody><tr></tr></tbody></table>", "<table><tbody><tr></tr></tbody></table>")]
    [InlineData("<body>a</body>b</body>", "a</body>b")]
    [InlineData("<body title='x>y'>z</body>", "z")]
    public void BodyContent_cases(string input, string expected) => Assert.Equal(expected, HtmlBody.BodyContent(input));

    [Fact]
    public void BodyContent_null() => Assert.Throws<ArgumentNullException>(() => HtmlBody.BodyContent(null!));

    [Fact]
    public void SignatureContent_cases()
    {
        Assert.Null(DraftBody.SignatureContent(null));
        Assert.Null(DraftBody.SignatureContent("<body> \r\n </body>"));
        Assert.Equal("<p>Sig</p>", DraftBody.SignatureContent("<body><p>Sig</p></body>"));
    }

    [Fact]
    public void Fragment_cases()
    {
        Assert.Equal("<p>Hi</p>", DraftBody.Fragment("Hi", false, null));
        Assert.Equal("<p>Hi</p><br><p>Sig</p>", DraftBody.Fragment("Hi", false, "<html><body><p>Sig</p></body></html>"));
        Assert.Equal("<b>x</b>", DraftBody.Fragment("<b>x</b>", true, null));
        Assert.Equal("<p>Sig</p>", DraftBody.Fragment("", false, "<body><p>Sig</p></body>"));
        Assert.Equal("<p>a&lt;b &amp; c</p>", DraftBody.Fragment("a<b & c", false, null));
        Assert.Equal("<p>Hi</p>", DraftBody.Fragment("Hi", false, "<body>  </body>"));
        Assert.Throws<ArgumentNullException>(() => DraftBody.Fragment(null!, false, null));
    }
}
