using ClippysRevenge.Core;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class EditTextsTests
{
    [Fact]
    public void Prepare_strips_reason_signature_quote_and_redacts()
    {
        var plain = "Clippy: why line\n\nHi Ann,\n\nCall me at +1 503 555 0100 or ann@x.org\n\nBest\n-- \nPeter\n\nOn Mon, Ann wrote:\n> old";
        Assert.Equal("Hi Ann,\n\nCall me at <phone> or <email>\n\nBest", EditTexts.Prepare(plain, null));
    }

    [Fact]
    public void Prepare_cuts_signature_text_and_quoted_header()
    {
        var plain = "Thanks, will do.\n\nPeter Dirk\nResearch IT\n\nFrom: Ann\nSent: Monday\nTo: Peter\nSubject: x\n\nold";
        Assert.Equal("Thanks, will do.", EditTexts.Prepare(plain, ["Peter Dirk\nResearch IT"]));
    }

    [Fact]
    public void Prepare_caps_at_1500_and_keeps_surrogate_pairs()
    {
        var long1 = EditTexts.Prepare(string.Concat(Enumerable.Repeat("abcd ", 400)), null);
        Assert.Equal(1500, long1.Length);
        Assert.EndsWith("…", long1);

        var s = new string('a', 1498) + "\U0001F44D" + "bbbb";
        var cut = EditTexts.Prepare(s, null);
        Assert.True(cut.Length <= 1500);
        Assert.EndsWith("…", cut);
        Assert.False(char.IsHighSurrogate(cut[^2]));
    }

    [Fact]
    public void Prepare_null_is_empty() => Assert.Equal("", EditTexts.Prepare(null, null));

    [Fact]
    public void Noted_wording()
    {
        Assert.Equal("AI drafts: noted how you edited 1 sent draft.", EditTexts.Noted(1));
        Assert.Equal("AI drafts: noted how you edited 3 sent drafts.", EditTexts.Noted(3));
        Assert.Equal("AI drafts: noted how you edited 1,234 sent drafts.", EditTexts.Noted(1234));
    }
}
