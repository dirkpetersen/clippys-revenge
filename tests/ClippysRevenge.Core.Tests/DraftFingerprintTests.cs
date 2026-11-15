using System.Text.RegularExpressions;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class DraftFingerprintTests
{
    private static DraftFingerprintInput V1() =>
        new("Re:: Budget", "Ann <ann@x.org>", null, null, "<p>Hi</p>", null, 1, ["a.pdf", "b.txt"]);

    [Fact]
    public void Vector1()
    {
        Assert.Equal(
            "subject=11:Re:: Budget\nto=15:Ann <ann@x.org>\ncc=0:\nbcc=0:\nhtml=9:<p>Hi</p>\ncategories=0:\nimportance=1:1\nattachments=11:a.pdf\nb.txt\n",
            DraftFingerprint.Canonical(V1()));
        Assert.Equal("8f5553f4220aad0112ff7c40c6a5eb06450a542378a92935584f8e91ea6ddd34", DraftFingerprint.Compute(V1()));
    }

    [Fact]
    public void Vector2_all_null()
    {
        const string expected = "f9a61f0e6b691e29895b32801ee08f50a047434b7a4df839465741743d766f15";
        Assert.Equal(expected, DraftFingerprint.Compute(new(null, null, null, null, null, null, 0, null)));
        Assert.Equal(expected, DraftFingerprint.Compute(new(null, null, null, null, null, null, 0, [])));
    }

    [Fact]
    public void Vector3_non_ascii()
    {
        var input = new DraftFingerprintInput("Re:: Grüße", null, null, null, null, null, 2, null);
        Assert.StartsWith("subject=10:", DraftFingerprint.Canonical(input));
        Assert.Equal("ac781f1735f02e1a81be347f35cc191e6d66ff96b84b3aacb75fb9cd12cac54c", DraftFingerprint.Compute(input));
    }

    [Fact]
    public void Stable_and_valid()
    {
        var a = DraftFingerprint.Compute(V1());
        Assert.Equal(a, DraftFingerprint.Compute(V1()));
        Assert.Matches(new Regex("^[0-9a-f]{64}\\z"), a);
        Assert.True(DraftFingerprint.IsValid(a));
    }

    public static TheoryData<string, DraftFingerprintInput> Changes() => new()
    {
        { "subject", V1() with { Subject = "Re:: Budget " } },
        { "to", V1() with { To = "ann@x.org" } },
        { "cc", V1() with { Cc = "x@y.z" } },
        { "bcc", V1() with { Bcc = "x@y.z" } },
        { "html", V1() with { HtmlBody = "<p>Hi!</p>" } },
        { "html crlf", V1() with { HtmlBody = "<p>Hi</p>\r\n" } },
        { "categories", V1() with { Categories = "Red" } },
        { "importance", V1() with { Importance = 2 } },
        { "attachment order", V1() with { AttachmentNames = ["b.txt", "a.pdf"] } },
        { "attachment count", V1() with { AttachmentNames = ["a.pdf"] } },
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public void Any_single_change_changes_the_hash(string _, DraftFingerprintInput changed) =>
        Assert.NotEqual(DraftFingerprint.Compute(V1()), DraftFingerprint.Compute(changed));

    [Fact]
    public void Length_prefix_separates_fields()
    {
        var a = new DraftFingerprintInput("a", "b", null, null, null, null, 0, null);
        var b = new DraftFingerprintInput("a\nto=1:b", null, null, null, null, null, 0, null);
        Assert.NotEqual(DraftFingerprint.Canonical(a), DraftFingerprint.Canonical(b));
        Assert.NotEqual(DraftFingerprint.Compute(a), DraftFingerprint.Compute(b));
        var c = new DraftFingerprintInput(null, null, "x", null, null, null, 0, null);
        var d = new DraftFingerprintInput(null, null, null, "x", null, null, 0, null);
        Assert.NotEqual(DraftFingerprint.Compute(c), DraftFingerprint.Compute(d));
    }

    [Fact]
    public void Null_equals_empty()
    {
        Assert.Equal(DraftFingerprint.Compute(V1() with { Subject = null }), DraftFingerprint.Compute(V1() with { Subject = "" }));
        Assert.Equal(DraftFingerprint.Compute(V1() with { AttachmentNames = ["a.pdf", null] }),
            DraftFingerprint.Compute(V1() with { AttachmentNames = ["a.pdf", ""] }));
    }

    [Fact]
    public void Negative_importance_is_invariant_decimal() =>
        Assert.Contains("importance=2:-1\n", DraftFingerprint.Canonical(V1() with { Importance = -1 }));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", false)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    public void IsValid(string? value, bool expected) => Assert.Equal(expected, DraftFingerprint.IsValid(value));

    [Fact]
    public void Null_input_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DraftFingerprint.Compute(null!));
        Assert.Throws<ArgumentNullException>(() => DraftFingerprint.Canonical(null!));
    }
}
