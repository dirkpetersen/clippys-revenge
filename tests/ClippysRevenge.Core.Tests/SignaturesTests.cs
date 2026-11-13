using System.Security;
using System.Text;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests;

public sealed class SignaturesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-m73-" + Guid.NewGuid());
    private readonly string _dir;

    public SignaturesTests()
    {
        _dir = Path.Combine(_root, "sig");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private Signatures Make(Func<SignatureKind, string?> src) => new(src, _dir);
    private void Put(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_dir, name + ".htm"), bytes);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_names_give_null(string? name) => Assert.Null(Make(_ => name).Default(SignatureKind.New));

    [Fact]
    public void Plain_utf8()
    {
        const string html = "<html><body>Hi é</body></html>";
        Put("Work", Encoding.UTF8.GetBytes(html));
        Assert.Equal(html, Make(_ => "Work").Default(SignatureKind.New));
    }

    [Fact]
    public void Utf8_bom_removed()
    {
        Put("Work", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("<body>x</body>")]);
        Assert.Equal("<body>x</body>", Make(_ => "Work").Default(SignatureKind.New));
    }

    [Fact]
    public void Utf16le_bom()
    {
        Put("Work", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("<body>é</body>")]);
        Assert.Equal("<body>é</body>", Make(_ => "Work").Default(SignatureKind.New));
    }

    [Fact]
    public void Windows1252_declared()
    {
        var head = Encoding.ASCII.GetBytes("<meta http-equiv=Content-Type content=\"text/html; charset=windows-1252\"><body>caf");
        Put("Work", [.. head, 0xE9, .. Encoding.ASCII.GetBytes("</body>")]);
        Assert.Contains("café", Make(_ => "Work").Default(SignatureKind.New));
    }

    [Fact]
    public void Invalid_utf8_falls_back_to_1252()
    {
        Put("Work", [.. Encoding.ASCII.GetBytes("<body>caf"), 0xE9, .. Encoding.ASCII.GetBytes("</body>")]);
        Assert.Equal("<body>café</body>", Make(_ => "Work").Default(SignatureKind.New));
    }

    [Fact]
    public void Unknown_charset_falls_through_to_utf8()
    {
        const string html = "<meta charset=x-bogus><body>é</body>";
        Put("Work", Encoding.UTF8.GetBytes(html));
        Assert.Equal(html, Make(_ => "Work").Default(SignatureKind.New));
    }

    [Fact]
    public void Kind_routing()
    {
        Put("A", Encoding.UTF8.GetBytes("a"));
        Put("B", Encoding.UTF8.GetBytes("b"));
        var s = Make(k => k == SignatureKind.New ? "A" : "B");
        Assert.Equal("a", s.Default(SignatureKind.New));
        Assert.Equal("b", s.Default(SignatureKind.Reply));
    }

    [Theory]
    [InlineData(@"..\x")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a|b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a\tb")]
    public void Bad_names_give_null(string name)
    {
        File.WriteAllBytes(Path.Combine(_root, "x.htm"), Encoding.UTF8.GetBytes("secret"));
        Assert.Null(Make(_ => name).Default(SignatureKind.New));
    }

    [Fact]
    public void Too_long_name() => Assert.Null(Make(_ => new string('a', 201)).Default(SignatureKind.New));

    [Fact]
    public void Missing_and_directory()
    {
        Assert.Null(Make(_ => "Nope").Default(SignatureKind.New));
        Directory.CreateDirectory(Path.Combine(_dir, "Work.htm"));
        Assert.Null(Make(_ => "Work").Default(SignatureKind.New));
    }

    [Fact]
    public void Size_limit()
    {
        Put("Big", new byte[Signatures.MaxFileBytes + 1]);
        Put("Ok", Enumerable.Repeat((byte)'a', Signatures.MaxFileBytes).ToArray());
        Assert.Null(Make(_ => "Big").Default(SignatureKind.New));
        Assert.NotNull(Make(_ => "Ok").Default(SignatureKind.New));
    }

    [Fact]
    public void Name_source_exceptions()
    {
        Assert.Null(Make(_ => throw new IOException()).Default(SignatureKind.New));
        Assert.Null(Make(_ => throw new UnauthorizedAccessException()).Default(SignatureKind.New));
        Assert.Null(Make(_ => throw new SecurityException()).Default(SignatureKind.New));
        Assert.Throws<InvalidOperationException>(() => Make(_ => throw new InvalidOperationException()).Default(SignatureKind.New));
    }

    [Fact]
    public void Undefined_kind() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(_ => "x").Default((SignatureKind)9));

    [Fact]
    public void Ctor_checks()
    {
        Assert.Throws<ArgumentNullException>(() => new Signatures(null!, _dir));
        Assert.Throws<ArgumentNullException>(() => new Signatures(_ => null, null!));
        Assert.Throws<ArgumentException>(() => new Signatures(_ => null, "sig"));
    }

    [Fact]
    public void Name_is_trimmed()
    {
        Put("My Sig", Encoding.UTF8.GetBytes("m"));
        Assert.Equal("m", Make(_ => "  My Sig  ").Default(SignatureKind.New));
    }

    [Fact]
    public void NameFromRegistryValue_cases()
    {
        Assert.Equal("Work", Signatures.NameFromRegistryValue("Work"));
        Assert.Equal("Work", Signatures.NameFromRegistryValue("  Work "));
        Assert.Null(Signatures.NameFromRegistryValue("  "));
        Assert.Null(Signatures.NameFromRegistryValue(null));
        Assert.Null(Signatures.NameFromRegistryValue(5));
        Assert.Equal("Work", Signatures.NameFromRegistryValue(Encoding.Unicode.GetBytes("Work\0")));
        Assert.Equal("Work", Signatures.NameFromRegistryValue(Encoding.Unicode.GetBytes("Work\0junk")));
        Assert.Null(Signatures.NameFromRegistryValue(new byte[0]));
        Assert.Null(Signatures.NameFromRegistryValue(Encoding.Unicode.GetBytes("\0")));
    }

    [Fact]
    public void DefaultDirectory_shape()
    {
        var d = Signatures.DefaultDirectory();
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), d);
        Assert.EndsWith("Microsoft" + Path.DirectorySeparatorChar + "Signatures", d);
    }
}
