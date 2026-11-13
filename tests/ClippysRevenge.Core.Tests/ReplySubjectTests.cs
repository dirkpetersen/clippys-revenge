namespace ClippysRevenge.Core.Tests;

public class ReplySubjectTests
{
    public static TheoryData<string?, string> Cases => new()
    {
        { "RE: Budget", "Re:: Budget" },
        { "re: Budget", "Re:: Budget" },
        { "Re: Re: Budget", "Re:: Budget" },
        { "Re: Re:", "Re::" },
        { "FW: Budget", "Re:: Budget" },
        { "Fwd: Re: Budget", "Re:: Budget" },
        { "AW: Budget", "Re:: Budget" },
        { "WG: AW: Re: Budget", "Re:: Budget" },
        { "Re:: Budget", "Re:: Budget" },
        { "Re::", "Re::" },
        { "RE:Budget", "Re:: Budget" },
        { "fwd:: Budget", "Re:: Budget" },
        { "  RE:   Budget  ", "Re:: Budget" },
        { "Budget", "Re:: Budget" },
        { "Reply needed", "Re:: Reply needed" },
        { "Budget Re: Q3", "Re:: Budget Re: Q3" },
        { "Re[2]: Budget", "Re:: Re[2]: Budget" },
        { "", "Re::" },
        { "   ", "Re::" },
        { null, "Re::" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Normalize_Expected(string? input, string expected) =>
        Assert.Equal(expected, ReplySubject.Normalize(input));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Normalize_IsIdempotent(string? input, string _)
    {
        var once = ReplySubject.Normalize(input);
        Assert.Equal(once, ReplySubject.Normalize(once));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Normalize_NonEmpty_StartsWithDoubleColonPrefix(string? input, string _)
    {
        Assert.Equal("Re:: ", ReplySubject.Prefix);
        var r = ReplySubject.Normalize(input);
        if (r != "Re::") Assert.StartsWith("Re:: ", r, StringComparison.Ordinal);
    }
}
