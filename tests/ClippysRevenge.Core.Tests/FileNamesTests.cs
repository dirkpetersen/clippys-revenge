namespace ClippysRevenge.Core.Tests;

public sealed class FileNamesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("clippy-m11-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    public static TheoryData<string?, string> Cases => new()
    {
        { "report.pdf", "report.pdf" },
        { "a\\b/c:d*e?f\"g<h>i|j.txt", "a_b_c_d_e_f_g_h_i_j.txt" },
        { "tab\there\n.txt", "tab_here_.txt" },
        { "..\\..\\evil.exe", ".._.._evil.exe" },
        { "  name.txt  ", "name.txt" },
        { "name. . ", "name" },
        { "...", "attachment" },
        { "", "attachment" },
        { "   ", "attachment" },
        { null, "attachment" },
        { "???", "___" },
        { "CON", "_CON" },
        { "con.txt", "_con.txt" },
        { "Lpt1.tar.gz", "_Lpt1.tar.gz" },
        { "CONSOLE.txt", "CONSOLE.txt" },
        { ".env", ".env" },
        { "bob.sample@example.edu", "bob.sample@example.edu" },
        { "Résumé – final.docx", "Résumé – final.docx" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void SafeFileName_Expected(string? input, string expected) =>
        Assert.Equal(expected, FileNames.SafeFileName(input));

    [Theory]
    [MemberData(nameof(Cases))]
    public void SafeFileName_IsIdempotent(string? input, string _)
    {
        var once = FileNames.SafeFileName(input);
        Assert.Equal(once, FileNames.SafeFileName(once));
    }

    [Fact]
    public void SafeFileName_LongName_KeepsExtension()
    {
        var r = FileNames.SafeFileName(new string('a', 300) + ".pdf");
        Assert.Equal(200, r.Length);
        Assert.EndsWith(".pdf", r);
        Assert.StartsWith(new string('a', 196), r);
        Assert.Equal(r, FileNames.SafeFileName(r));
    }

    [Fact]
    public void SafeFileName_LongName_HugeExtension()
    {
        var input = "x." + new string('b', 300);
        var r = FileNames.SafeFileName(input);
        Assert.Equal(200, r.Length);
        Assert.Equal(input[..200], r);
        Assert.Equal(r, FileNames.SafeFileName(r));
    }

    [Fact]
    public void SafeFileName_LongName_NoSplitSurrogate()
    {
        var r = FileNames.SafeFileName(new string('a', 195) + string.Concat(Enumerable.Repeat("😀", 10)) + ".pdf");
        Assert.True(r.Length <= 200);
        Assert.EndsWith(".pdf", r);
        Assert.False(char.IsHighSurrogate(r[^5]));
        Assert.Equal(r, FileNames.SafeFileName(r));
    }

    [Fact]
    public void UniquePath_FreeName_ReturnsCombined() =>
        Assert.Equal(Path.Combine(_dir, "a.pdf"), FileNames.UniquePath(_dir, "a.pdf"));

    [Fact]
    public void UniquePath_Existing_AddsCounterStartingAt2()
    {
        File.WriteAllText(Path.Combine(_dir, "a.pdf"), "");
        Assert.Equal(Path.Combine(_dir, "a (2).pdf"), FileNames.UniquePath(_dir, "a.pdf"));
        File.WriteAllText(Path.Combine(_dir, "a (2).pdf"), "");
        Assert.Equal(Path.Combine(_dir, "a (3).pdf"), FileNames.UniquePath(_dir, "a.pdf"));
    }

    [Fact]
    public void UniquePath_ExistingDirectory_CountsAsTaken()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a.pdf"));
        Assert.Equal(Path.Combine(_dir, "a (2).pdf"), FileNames.UniquePath(_dir, "a.pdf"));
    }

    [Fact]
    public void UniquePath_MultiDotName()
    {
        File.WriteAllText(Path.Combine(_dir, "r.tar.gz"), "");
        Assert.Equal(Path.Combine(_dir, "r.tar (2).gz"), FileNames.UniquePath(_dir, "r.tar.gz"));
    }

    [Fact]
    public void UniquePath_DotFile()
    {
        File.WriteAllText(Path.Combine(_dir, ".bashrc"), "");
        Assert.Equal(Path.Combine(_dir, ".bashrc (2)"), FileNames.UniquePath(_dir, ".bashrc"));
    }

    [Fact]
    public void UniquePath_NoExtension()
    {
        File.WriteAllText(Path.Combine(_dir, "README"), "");
        Assert.Equal(Path.Combine(_dir, "README (2)"), FileNames.UniquePath(_dir, "README"));
    }

    [Fact]
    public void UniquePath_SanitizesName()
    {
        var r = FileNames.UniquePath(_dir, "..\\x?.txt");
        Assert.Equal(Path.Combine(_dir, ".._x_.txt"), r);
        Assert.Equal(_dir, Path.GetDirectoryName(r));
    }

    [Fact]
    public void UniquePath_MissingDir_ReturnsFirstCandidate()
    {
        var missing = Path.Combine(_dir, "nope");
        Assert.Equal(Path.Combine(missing, "a.pdf"), FileNames.UniquePath(missing, "a.pdf"));
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void UniquePath_BadDir_Throws(string? dir) =>
        Assert.Throws<ArgumentException>(() => FileNames.UniquePath(dir!, "a.pdf"));
}
