namespace ClippysRevenge.Core.Tests;

public class HtmlBodyTests
{
    [Theory]
    [InlineData("a < b & \"c\" 'd' > e", "<p>a &lt; b &amp; &quot;c&quot; &#39;d&#39; &gt; e</p>")]
    [InlineData("Hi Bob,\n\nThanks.\nSee you", "<p>Hi Bob,</p><p>Thanks.<br>See you</p>")]
    [InlineData("a\r\nb\r\n\r\nc", "<p>a<br>b</p><p>c</p>")]
    [InlineData("a\rb", "<p>a<br>b</p>")]
    [InlineData("a\n\n\n\nb", "<p>a</p><p>b</p>")]
    [InlineData("a\n  \t\nb", "<p>a</p><p>b</p>")]
    [InlineData("\n\na\n\n", "<p>a</p>")]
    [InlineData("", "")]
    [InlineData("\n \r\n\t", "")]
    [InlineData("Grüße 😀", "<p>Grüße 😀</p>")]
    [InlineData("&amp;", "<p>&amp;amp;</p>")]
    [InlineData("a  b\t c ", "<p>a  b\t c </p>")]
    [InlineData("line", "<p>line</p>")]
    public void FromPlainText_Works(string input, string expected)
        => Assert.Equal(expected, HtmlBody.FromPlainText(input));

    [Fact]
    public void FromPlainText_UrlsNotLinkified()
    {
        var r = HtmlBody.FromPlainText("See https://x.com/?a=1&b=<2>");
        Assert.Equal("<p>See https://x.com/?a=1&amp;b=&lt;2&gt;</p>", r);
        Assert.DoesNotContain("<a", r);
    }

    [Fact]
    public void FromPlainText_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => HtmlBody.FromPlainText(null!));

    private const string F = "<p>New</p>";

    [Theory]
    [InlineData("<html><head></head><BODY class=MsoNormal lang=EN-US>X</BODY></html>", "<html><head></head><BODY class=MsoNormal lang=EN-US><p>New</p>X</BODY></html>")]
    [InlineData("<html><body>X</body></html>", "<html><body><p>New</p>X</body></html>")]
    [InlineData("<Body style='a>b' lang=\"x>y\">X", "<Body style='a>b' lang=\"x>y\"><p>New</p>X")]
    [InlineData("<body\r\nlang=EN-US>X", "<body\r\nlang=EN-US><p>New</p>X")]
    [InlineData("<div>X</div>", "<p>New</p><div>X</div>")]
    [InlineData("<table><tbody><tr><td>X</td></tr></tbody></table>", "<p>New</p><table><tbody><tr><td>X</td></tr></tbody></table>")]
    [InlineData("<p><bodyguard>X</p>", "<p>New</p><p><bodyguard>X</p>")]
    [InlineData("<html><body><table><tbody>X</tbody></table></body></html>", "<html><body><p>New</p><table><tbody>X</tbody></table></body></html>")]
    [InlineData("<body>A<body>B", "<body><p>New</p>A<body>B")]
    [InlineData("<head><style>body{margin:0}</style></head><body>X", "<head><style>body{margin:0}</style></head><body><p>New</p>X")]
    [InlineData("<body class=x", "<p>New</p><body class=x")]
    [InlineData("", "<p>New</p>")]
    public void InsertAtTop_Works(string html, string expected)
        => Assert.Equal(expected, HtmlBody.InsertAtTop(html, F));

    [Fact]
    public void InsertAtTop_EmptyFragment_Unchanged()
        => Assert.Equal("<body>X", HtmlBody.InsertAtTop("<body>X", ""));

    [Fact]
    public void InsertAtTop_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HtmlBody.InsertAtTop(null!, F));
        Assert.Throws<ArgumentNullException>(() => HtmlBody.InsertAtTop("x", null!));
    }

    [Fact]
    public void Combined()
        => Assert.Equal(
            "<html><body lang=EN-US><p>Thanks &amp; bye</p><div>Original</div></body></html>",
            HtmlBody.InsertAtTop("<html><body lang=EN-US><div>Original</div></body></html>", HtmlBody.FromPlainText("Thanks & bye")));
}
