namespace ClippysRevenge.Core.Tests;

public class DraftReasonLineTests
{
    private const string Open = "<p><span style=\"background-color:#ffff00\">Clippy: ";
    private const string Close = "</span></p>";

    private static string L(string? note) => DraftReasonLine.Html(note);

    /// <summary>The escaped text inside the line ("" when there is no line).</summary>
    private static string T(string? note)
    {
        var h = L(note);
        if (h.Length == 0) return "";
        Assert.StartsWith(Open, h);
        Assert.EndsWith(Close, h);
        return h[Open.Length..^Close.Length];
    }

    [Fact]
    public void Plain_note_gives_the_exact_line() =>
        Assert.Equal(Open + "Ricky asked about the Saturday bus; this answers that." + Close,
            L("Ricky asked about the Saturday bus; this answers that."));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t\r\n ")]
    public void Blank_gives_nothing(string? note) => Assert.Equal("", L(note));

    [Fact]
    public void Text_is_escaped_without_entities_passing_through() =>
        Assert.Equal("a &lt;b&gt;x&lt;/b&gt; &amp; &quot;q&quot; &amp;lt;", T("a <b>x</b> & \"q\" &lt;"));

    [Fact]
    public void Html_injection_is_inert()
    {
        var h = L("</span></p><a href=\"javascript:x\">click</a><img src=x onerror=y>");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(h, "<span"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(h, "</span>"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(h, "<p>"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(h, "</p>"));
        Assert.DoesNotContain("<a", h);
        Assert.DoesNotContain("<img", h);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(h, "style="));
    }

    [Fact]
    public void Controls_and_whitespace_collapse() =>
        Assert.Equal("line1 line2 end x", T("line1\r\nline2\t\u0007end    x"));

    [Fact]
    public void Format_characters_and_lone_surrogates_are_removed()
    {
        Assert.Equal("abcdef", T("ab\u202Ecd\u200Bef\uFEFF"));
        Assert.Equal("abcd", T("ab\uD800cd"));
        Assert.Equal("ab\uDC00cd".Replace("\uDC00", ""), T("ab\uDC00cd"));
        Assert.Equal("a\U0001F600b", T("a\U0001F600b"));
    }

    [Fact]
    public void Urls_become_link_text()
    {
        Assert.Equal("See [link] now", T("See https://evil.example/login?x=1 now"));
        foreach (var u in new[] { "HTTP://A.B", "www.evil.example", "mailto:a@b.c", "file://c/x", "ftp://h/f" })
            Assert.Equal("[link]", T(u));
        Assert.Contains("Clippy: [link]", L("https://x.y"));
        var h = L("a http://x.y/z b www.q.r c ftp://f.g");
        Assert.DoesNotContain("://", h);
        Assert.DoesNotContain("www.", h);
    }

    [Fact]
    public void The_models_own_prefix_is_removed()
    {
        Assert.Equal("Ann asked", T("Clippy: Ann asked"));
        Assert.Equal("Ann asked", T("clippy:Ann asked"));
        Assert.Equal("x", T("Clippy: clippy: x"));
        Assert.Equal("", L("Clippy:"));
        Assert.Equal("", L("Clippy: clippy:"));
    }

    [Fact]
    public void Cut_is_at_160_characters_and_before_escaping()
    {
        Assert.Equal(new string('a', 160), T(new string('a', 160)));
        Assert.Equal(new string('a', 160) + "…", T(new string('a', 161)));
        Assert.Equal(new string('a', 159) + "&amp;…", T(new string('a', 159) + "&bbbbbbbb"));
    }

    [Fact]
    public void Cut_does_not_split_a_surrogate_pair() =>
        Assert.Equal(new string('a', 159) + "…", T(new string('a', 159) + "\U0001F600zz"));

    [Fact]
    public void Only_the_first_640_characters_are_read()
    {
        Assert.Equal("", L(new string(' ', 10_000) + "late"));
        var t = T("x" + new string('y', 100_000));
        Assert.Equal(160 + 1, t.Length);
        Assert.EndsWith("…", t);
    }

    [Fact]
    public void Whitespace_is_collapsed_after_the_cut_input_not_before() =>
        Assert.Equal(new string('a', 100) + " b", T(new string('a', 100) + new string(' ', 300) + "b"));

    [Fact]
    public void StripPlain_null_and_unchanged()
    {
        Assert.Equal("", DraftReasonLine.StripPlain(null));
        var s = "Hi\nBye";
        Assert.Same(s, DraftReasonLine.StripPlain(s));
    }

    [Theory]
    [InlineData("Clippy: x\r\n\r\nHi Ricky,\r\nyes.", "Hi Ricky,\nyes.")]
    [InlineData("\r\n  Clippy: x\nClippy: y\n\nHi", "Hi")]
    [InlineData("Clippy: x\nHi", "Hi")]
    [InlineData("Clippy: only", "")]
    [InlineData("Clippy:\u00A0x\nHi", "Hi")]
    public void StripPlain_drops_leading_clippy_lines(string input, string expected) =>
        Assert.Equal(expected, DraftReasonLine.StripPlain(input));

    [Theory]
    [InlineData("Hi\nClippy: x")]
    [InlineData("Clippy:x\nHi")]
    [InlineData("clippy: x\nHi")]
    [InlineData("Clippy:\nHi")]
    public void StripPlain_keeps_everything_else(string input) => Assert.Same(input, DraftReasonLine.StripPlain(input));

    [Fact]
    public void Round_trip_through_plain_text() =>
        Assert.Equal("Hi", DraftReasonLine.StripPlain(DraftHtml.ToPlainText(L("Ann asked") + "<p>Hi</p>")));
}
