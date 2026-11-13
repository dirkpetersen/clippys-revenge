namespace ClippysRevenge.Core.Tests;

public class SemVersionTests
{
    [Theory]
    [InlineData("0.0.0", "0.0.0")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("10.20.30", "10.20.30")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.1")]
    [InlineData("1.0.0-0.3.7", "1.0.0-0.3.7")]
    [InlineData("1.0.0-x.7.z.92", "1.0.0-x.7.z.92")]
    [InlineData("1.0.0+20130313144700", "1.0.0")]
    [InlineData("1.0.0-beta+exp.sha.5114f85", "1.0.0-beta")]
    public void TryParse_Accepts(string text, string toString)
    {
        Assert.True(SemVersion.TryParse(text, out var v));
        Assert.Equal(toString, v.ToString());
        Assert.Equal(toString, SemVersion.Parse(text).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" 1.2.3")]
    [InlineData("v1.2.3")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("2147483648.0.0")]
    [InlineData("1.2.3\n")]
    public void TryParse_Rejects(string? text)
    {
        Assert.False(SemVersion.TryParse(text, out var v));
        Assert.Null(v);
        Assert.Throws<FormatException>(() => SemVersion.Parse(text!));
    }

    [Fact]
    public void TryParse_RejectsLongString()
    {
        Assert.False(SemVersion.TryParse("1.0.0-" + new string('a', 294), out _));
        Assert.False(SemVersion.TryParse(new string('1', 300), out _));
    }

    [Fact]
    public void PrecedenceChain_IsStrictlyIncreasing()
    {
        string[] chain =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11",
            "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0",
        ];
        var versions = chain.Select(SemVersion.Parse).ToArray();
        for (var i = 0; i < versions.Length; i++)
        {
            for (var j = 0; j < versions.Length; j++)
            {
                Assert.Equal(Math.Sign(i.CompareTo(j)), Math.Sign(versions[i].CompareTo(versions[j])));
                Assert.Equal(i < j, versions[i] < versions[j]);
                Assert.Equal(i > j, versions[i] > versions[j]);
                Assert.Equal(i <= j, versions[i] <= versions[j]);
                Assert.Equal(i >= j, versions[i] >= versions[j]);
                Assert.Equal(i == j, versions[i] == versions[j]);
                Assert.Equal(i != j, versions[i] != versions[j]);
            }
        }
    }

    [Fact]
    public void Comparisons_Misc()
    {
        Assert.True(SemVersion.Parse("0.0.6-beta.1") < SemVersion.Parse("0.0.6"));
        Assert.True(SemVersion.Parse("0.0.10") > SemVersion.Parse("0.0.9"));
        Assert.Equal(SemVersion.Parse("1.0.0+a"), SemVersion.Parse("1.0.0+b"));
        Assert.True(SemVersion.Parse("1.0.0+a") == SemVersion.Parse("1.0.0+b"));
        Assert.Equal(SemVersion.Parse("1.0.0-rc.1").GetHashCode(), SemVersion.Parse("1.0.0-rc.1+x").GetHashCode());
        Assert.Equal(SemVersion.Parse("1.2.3").GetHashCode(), SemVersion.Parse("1.2.3+meta").GetHashCode());
        Assert.True(SemVersion.Parse("1.0.0-99999999999999999999") < SemVersion.Parse("1.0.0-100000000000000000000"));
    }

    [Fact]
    public void Null_SortsBeforeEverything()
    {
        SemVersion? none = null;
        Assert.True(none < SemVersion.Zero);
        Assert.True(SemVersion.Zero > none);
        Assert.True(SemVersion.Zero.CompareTo(null) > 0);
        Assert.True(none == null);
        Assert.False(none != null);
        Assert.False(SemVersion.Zero.Equals(null));
        SemVersion? other = null;
        Assert.True(none <= other);
        Assert.True(none >= other);
    }

    [Fact]
    public void Properties()
    {
        var v = SemVersion.Parse("1.2.3-beta.1");
        Assert.Equal(1, v.Major);
        Assert.Equal(2, v.Minor);
        Assert.Equal(3, v.Patch);
        Assert.Equal(["beta", "1"], v.PreRelease);
        Assert.True(v.IsPreRelease);
        Assert.False(SemVersion.Zero.IsPreRelease);
        Assert.Equal("0.0.0", SemVersion.Zero.ToString());
    }

    [Theory]
    [InlineData("v0.0.4", "0.0.4")]
    [InlineData("v12.3.45", "12.3.45")]
    public void ReleaseTag_Accepts(string tag, string expected)
    {
        Assert.True(SemVersion.TryParseReleaseTag(tag, out var v));
        Assert.Equal(expected, v.ToString());
    }

    [Theory]
    [InlineData("0.0.4")]
    [InlineData("V0.0.4")]
    [InlineData("v0.0.4-beta.1")]
    [InlineData("v0.0.4+x")]
    [InlineData("v01.0.0")]
    [InlineData("v0.0")]
    [InlineData("v1234567890.0.0")]
    [InlineData("v0.0.4\n")]
    [InlineData("v1١.0.0")]
    [InlineData("v1.0.٣")]
    [InlineData(null)]
    public void ReleaseTag_Rejects(string? tag)
    {
        Assert.False(SemVersion.TryParseReleaseTag(tag, out var v));
        Assert.Null(v);
    }

    [Theory]
    [InlineData("1١.0.0")]
    [InlineData("1.0.0-١")]
    public void Parse_rejects_non_ascii_digits(string text)
    {
        Assert.False(SemVersion.TryParse(text, out var v));
        Assert.Null(v);
    }

    [Fact]
    public void AppVersionInfo_Parse_WithCommit()
    {
        var a = AppVersionInfo.Parse("0.0.4+ce89762560811e819f6ecb5385df93622cee00e5");
        Assert.Equal("0.0.4", a.Version.ToString());
        Assert.Equal("ce89762560811e819f6ecb5385df93622cee00e5", a.Commit);
        Assert.Equal("ce89762", a.ShortCommit);
    }

    [Fact]
    public void AppVersionInfo_Parse_Variants()
    {
        var pre = AppVersionInfo.Parse("0.0.6-beta.1+ABCDEF1");
        Assert.Equal("0.0.6-beta.1", pre.Version.ToString());
        Assert.Equal("abcdef1", pre.Commit);

        var plain = AppVersionInfo.Parse("0.0.5");
        Assert.Equal("0.0.5", plain.Version.ToString());
        Assert.Null(plain.Commit);
        Assert.Null(plain.ShortCommit);

        Assert.Null(AppVersionInfo.Parse("0.0.5+local").Commit);
        Assert.Equal("0.0.0", AppVersionInfo.Parse("garbage").Version.ToString());
        Assert.Same(AppVersionInfo.Unknown, AppVersionInfo.Parse(null));
        Assert.Same(AppVersionInfo.Unknown, AppVersionInfo.Parse("   "));
        Assert.Equal("0.0.5", AppVersionInfo.Parse("  0.0.5  ").Version.ToString());
    }

    [Fact]
    public void AppVersionInfo_FromAssembly()
    {
        Assert.Same(AppVersionInfo.Unknown, AppVersionInfo.FromAssembly(null));
        var info = AppVersionInfo.FromAssembly(typeof(SemVersion).Assembly);
        Assert.True(SemVersion.TryParse(info.Version.ToString(), out _));
    }
}
