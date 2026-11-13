using System.Text.RegularExpressions;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests;

public class DraftHtmlTests
{
    private static string H(string x) => DraftHtml.HighlightOpen + x + DraftHtml.HighlightClose;

    public static TheoryData<string, string> SanitizeCases => new()
    {
        { "<P>a</P>", "<p>a</p>" },
        { "a<br/>b<BR>c<br />d", "a<br>b<br>c<br>d" },
        { "<ul><li>a</li><li>b</li></ul>", "<ul><li>a</li><li>b</li></ul>" },
        { "<ol><li>a</ol>", "<ol><li>a</li></ol>" },
        { "<b>a</b><strong>b</strong><i>c</i><em>d</em><u>e</u><blockquote>f</blockquote>", "<b>a</b><strong>b</strong><i>c</i><em>d</em><u>e</u><blockquote>f</blockquote>" },
        { "<p onclick=\"alert(1)\" class=x style='color:red' title=\"a>b\">a</p>", "<p>a</p>" },
        { "<script>alert(1)</script>Hi", "Hi" },
        { "<SCRIPT type=\"x\">a</script >b", "b" },
        { "<style>p{background:url(https://t/x)}</style>x", "x" },
        { "<script>never closed", "" },
        { "<a href=\"https://evil/\">click</a> here", "click here" },
        { "<img src=\"https://t/x.png\" alt=\"A\">Hi", "Hi" },
        { "<img src=cid:x onerror=alert(1)>", "" },
        { "<span style=\"background:url(https://x)\">a</span>", "a" },
        { "<div>a</div><div>b</div>", "a<br>b" },
        { "<p>a</p><div>b</div>", "<p>a</p>b" },
        { "<svg><script>x</script></svg>y", "y" },
        { "<iframe src=x>t</iframe>u", "u" },
        { "<head><title>T</title></head><body><p>a</p></body>", "<p>a</p>" },
        { "<scr<script>ipt>alert(1)</script>", "ipt&gt;alert(1)" },
        { "a &amp; b &lt; c &#169; &#x1F600; &nbsp;x &hellip;", "a &amp; b &lt; c &#169; &#x1F600; &nbsp;x &hellip;" },
        { "AT&T & co", "AT&amp;T &amp; co" },
        { "&bogus; &copy &#0; &#xD800; &#x110000;", "&amp;bogus; &amp;copy &amp;#0; &amp;#xD800; &amp;#x110000;" },
        { "1 < 2 > 0, a <3 b", "1 &lt; 2 &gt; 0, a &lt;3 b" },
        { "x <p", "x &lt;p" },
        { "<p><b>a", "<p><b>a</b></p>" },
        { "a</b></p></br>", "a" },
        { "<b><i>a</b>c</i>", "<b><i>a</i></b>c" },
        { "a<!-- x -->b<!DOCTYPE html><?xml v?>c", "abc" },
        { "a<!-- unterminated", "a" },
        { "a\u0001b\u007Fc", "abc" },
        { "<p>Hi [[date]] ok</p>", "<p>Hi " + H("[[date]]") + " ok</p>" },
        { "[[R&D budget]]", H("[[R&amp;D budget]]") },
        { "[[]] [[a\nb]] [[a<b>b</b>]]", "[[]] [[a\nb]] [[a<b>b</b>]]" },
        { "[[" + new string('x', 200) + "]]", H("[[" + new string('x', 200) + "]]") },
        { "[[" + new string('x', 201) + "]]", "[[" + new string('x', 201) + "]]" },
        { "<span style=\"background-color:#ffff00\">[[x]]</span>", H("[[x]]") },
        { "<p/>a", "<p>a</p>" },
        { "<div>\n</div>", "" },
    };

    [Theory]
    [MemberData(nameof(SanitizeCases))]
    public void Sanitize_cases(string input, string expected) =>
        Assert.Equal(expected, DraftHtml.Sanitize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<p></p>")]
    [InlineData("<br><br>")]
    [InlineData("<p>&nbsp;​</p>")]
    [InlineData("<script>x</script>")]
    public void Sanitize_no_visible_text_is_empty(string? input) =>
        Assert.Equal("", DraftHtml.Sanitize(input));

    [Fact]
    public void Sanitize_depth_is_capped()
    {
        string result = DraftHtml.Sanitize(string.Concat(Enumerable.Repeat("<b>", 200)) + "x");
        Assert.Equal(DraftHtml.MaxDepth, Regex.Matches(result, "<b>").Count);
        Assert.Equal(DraftHtml.MaxDepth, Regex.Matches(result, "</b>").Count);
        Assert.Contains("x", result);
    }

    [Fact]
    public void Sanitize_size_is_capped() =>
        Assert.Equal(200_000, DraftHtml.Sanitize(new string('a', 300_000)).Length);

    [Fact]
    public void Sanitize_many_unterminated_tags_is_fast()
    {
        string input = string.Concat(Enumerable.Repeat("<a\"", 60_000)) + ">\"";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DraftHtml.Sanitize(input);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
    }

    private static readonly HashSet<string> AllowedTagText = new(StringComparer.Ordinal)
    {
        "<p>", "</p>", "<br>", "<ul>", "</ul>", "<ol>", "</ol>", "<li>", "</li>", "<b>", "</b>",
        "<strong>", "</strong>", "<i>", "</i>", "<em>", "</em>", "<u>", "</u>", "<blockquote>", "</blockquote>",
        DraftHtml.HighlightOpen, DraftHtml.HighlightClose,
    };

    private static void AssertSafe(string result)
    {
        foreach (Match m in Regex.Matches(result, "<[^>]*>"))
            Assert.True(AllowedTagText.Contains(m.Value), $"unexpected tag {m.Value} in {result}");

        string stripped = result.Replace(DraftHtml.HighlightOpen, "").Replace(DraftHtml.HighlightClose, "");
        stripped = Regex.Replace(stripped, "</?(p|br|ul|ol|li|b|strong|i|em|u|blockquote)>", "");
        Assert.DoesNotContain("<", stripped);
        Assert.DoesNotMatch(@"<[^>]*(script|href|src|on\w+=)", result);
    }

    [Fact]
    public void Sanitize_never_throws_and_output_is_safe()
    {
        var rng = new Random(42);
        const string alphabet = "<>/!-?&#;\"'= abipscrtyle[]x\n";
        for (int n = 0; n < 5000; n++)
        {
            int len = rng.Next(0, 301);
            var chars = new char[len];
            for (int i = 0; i < len; i++) chars[i] = alphabet[rng.Next(alphabet.Length)];
            AssertSafe(DraftHtml.Sanitize(new string(chars)));
        }

        foreach (var c in SanitizeCases)
            AssertSafe(DraftHtml.Sanitize(c.Data.Item1));
    }

    // ---------------------------------------------------------------- ToPlainText

    [Theory]
    [InlineData("<html><head><style>p{}</style></head><body><p class=MsoNormal>Dirk Petersen<o:p></o:p></p><p>Research&nbsp;Computing</p></body></html>", "Dirk Petersen\nResearch Computing")]
    [InlineData("a<br>b", "a\nb")]
    [InlineData("a<br><br>b", "a\n\nb")]
    [InlineData("<div>a</div><div>b</div>", "a\nb")]
    [InlineData("a \n  b", "a b")]
    [InlineData("R&amp;D <span>x</span>", "R&D x")]
    [InlineData("<p>a</p><p></p><p></p><p>b</p>", "a\nb")]
    [InlineData("a<br><br><br><br>b", "a\n\nb")]
    [InlineData("<p>a</p> <p>b</p>", "a\nb")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void ToPlainText_cases(string? input, string expected) =>
        Assert.Equal(expected, DraftHtml.ToPlainText(input));
}
