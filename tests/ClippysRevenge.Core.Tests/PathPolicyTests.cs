using System.Diagnostics;

namespace ClippysRevenge.Core.Tests;

public class PathPolicyTests : IDisposable
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clippy-pp-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private readonly TempDir _temp = new();
    private readonly string T;
    private readonly string Docs;
    private readonly string Docs2;
    private readonly string[] roots;

    public PathPolicyTests()
    {
        T = _temp.Path;
        Docs = Path.Combine(T, "Docs");
        Docs2 = Path.Combine(T, "Docs2");
        Directory.CreateDirectory(Docs);
        Directory.CreateDirectory(Docs2);
        roots = [Docs];
    }

    public void Dispose() => _temp.Dispose();

    private static string Touch(params string[] parts)
    {
        var p = Path.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
        return p;
    }

    private static PathRejectReason Refused(string? path, IReadOnlyList<string> r, PathMode mode, string? relativeTo = null)
        => Assert.Throws<PathNotAllowedException>(() => PathPolicy.Check(path, r, mode, relativeTo)).Reason;

    private static void SymlinkOrSkip(Action create)
    {
        try { create(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("symlink creation not permitted");
        }
    }

    [Fact]
    public void Read_FileInsideRoot_ReturnsFullPath()
    {
        var f = Touch(Docs, "a.txt");
        Assert.Equal(Path.GetFullPath(f), PathPolicy.Check(f, roots, PathMode.Read));
    }

    [Fact]
    public void Read_FileInNestedFolder_Allowed()
    {
        var f = Touch(Docs, "sub", "b.txt");
        Assert.Equal(Path.GetFullPath(f), PathPolicy.Check(f, roots, PathMode.Read));
    }

    [Fact]
    public void Read_FileOutsideRoot_Refused()
    {
        var f = Touch(T, "outside.txt");
        Assert.Equal(PathRejectReason.OutsideAllowedFolders, Refused(f, roots, PathMode.Read));
    }

    [Fact]
    public void DotDotTraversal_Refused()
    {
        Touch(T, "outside.txt");
        var p = Path.Combine(Docs, "..", "outside.txt");
        Assert.Equal(PathRejectReason.OutsideAllowedFolders, Refused(p, roots, PathMode.Read));
    }

    [Fact]
    public void DotDotStayingInside_Allowed()
    {
        var f = Touch(Docs, "a.txt");
        Directory.CreateDirectory(Path.Combine(Docs, "sub"));
        var p = Path.Combine(Docs, "sub", "..", "a.txt");
        Assert.Equal(Path.GetFullPath(f), PathPolicy.Check(p, roots, PathMode.Read));
    }

    [Fact]
    public void PrefixTrick_Refused()
    {
        var f = Touch(Docs2, "x.txt");
        Assert.Equal(PathRejectReason.OutsideAllowedFolders, Refused(f, roots, PathMode.Read));
    }

    [Fact]
    public void RootItself_Write_Allowed()
    {
        Assert.Equal(Path.GetFullPath(Docs), PathPolicy.Check(Docs, roots, PathMode.Write));
    }

    [Fact]
    public void RootWithTrailingSeparator_Matches()
    {
        var f = Touch(Docs, "a.txt");
        var r = new[] { Docs + Path.DirectorySeparatorChar };
        Assert.Equal(Path.GetFullPath(f), PathPolicy.Check(f, r, PathMode.Read));
    }

    [Fact]
    public void CaseInsensitiveMatch()
    {
        var f = Touch(Docs, "a.txt");
        var r = new[] { Path.Combine(T, "DOCS") };
        Assert.Equal(Path.GetFullPath(f), PathPolicy.Check(f, r, PathMode.Read));
    }

    [Fact]
    public void Unc_Refused()
    {
        Assert.Equal(PathRejectReason.DeviceOrUnc, Refused(@"\\server\share\a.txt", roots, PathMode.Read));
    }

    [Fact]
    public void ForwardSlashUnc_Refused()
    {
        Assert.Equal(PathRejectReason.DeviceOrUnc, Refused("//server/share/a.txt", roots, PathMode.Read));
    }

    [Theory]
    [InlineData(@"\\?\C:\Docs\a.txt")]
    [InlineData(@"\\.\C:\Docs\a.txt")]
    public void DevicePrefix_Refused(string p)
    {
        Assert.Equal(PathRejectReason.DeviceOrUnc, Refused(p, roots, PathMode.Read));
    }

    [Theory]
    [InlineData("a.txt:secret")]
    [InlineData("a.txt::$DATA")]
    public void AlternateDataStream_Refused(string name)
    {
        Assert.Equal(PathRejectReason.AlternateDataStream, Refused(Path.Combine(Docs, name), roots, PathMode.Read));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("com1")]
    [InlineData("LPT9.log")]
    [InlineData("sub/AUX/x.txt")]
    public void ReservedDeviceName_Refused(string rel)
    {
        var p = Path.Combine(Docs, rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(PathRejectReason.ReservedDeviceName, Refused(p, roots, PathMode.Read));
    }

    [Fact]
    public void ReservedLookalike_Allowed()
    {
        var a = Touch(Docs, "CONSOLE.txt");
        var b = Touch(Docs, "null.txt");
        Assert.Equal(Path.GetFullPath(a), PathPolicy.Check(a, roots, PathMode.Read));
        Assert.Equal(Path.GetFullPath(b), PathPolicy.Check(b, roots, PathMode.Read));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Refused(string? p)
    {
        Assert.Equal(PathRejectReason.Empty, Refused(p, roots, PathMode.Read));
    }

    [Theory]
    [InlineData("a\0.txt")]
    [InlineData("a\n.txt")]
    public void ControlChar_Refused(string name)
    {
        Assert.Equal(PathRejectReason.InvalidCharacters, Refused(Docs + Path.DirectorySeparatorChar + name, roots, PathMode.Read));
    }

    [Fact]
    public void Relative_WithoutBase_Refused()
    {
        Assert.Equal(PathRejectReason.NotFullyQualified, Refused("a.txt", roots, PathMode.Read));
    }

    [Fact]
    public void Relative_WithBase_ResolvesUnderBase()
    {
        var r = PathPolicy.Check("out", roots, PathMode.Write, Docs);
        Assert.Equal(Path.GetFullPath(Path.Combine(Docs, "out")), r);
        Assert.True(Directory.Exists(r));
    }

    [Fact]
    public void Relative_WithBase_TraversalOut_Refused()
    {
        var rel = Path.Combine("..", "..", "evil");
        Assert.Equal(PathRejectReason.OutsideAllowedFolders, Refused(rel, roots, PathMode.Write, Docs));
        Assert.False(Directory.Exists(Path.GetFullPath(Path.Combine(Docs, rel))));
    }

    [Fact]
    public void RelativeTo_NotFullyQualified_Throws()
    {
        Assert.Throws<ArgumentException>(() => PathPolicy.Check("a.txt", roots, PathMode.Read, "rel"));
    }

    [Fact]
    public void NullRoots_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PathPolicy.Check(Path.Combine(Docs, "a.txt"), null!, PathMode.Read));
    }

    [Fact]
    public void NoUsableRoots_Refused()
    {
        var p = Path.Combine(Docs, "a.txt");
        Assert.Equal(PathRejectReason.NoAllowedFolders, Refused(p, [], PathMode.Read));
        Assert.Equal(PathRejectReason.NoAllowedFolders, Refused(p, ["", "relative", @"\\srv\share"], PathMode.Read));
    }

    [Fact]
    public void LongestRootWins()
    {
        var f = Touch(Docs, "a.txt");
        Assert.Equal(Path.GetFullPath(f), PathPolicy.Check(f, [T, Docs], PathMode.Read));
    }

    [Fact]
    public void Read_Missing_ThrowsFileNotFound()
    {
        var p = Path.Combine(Docs, "missing.txt");
        var ex = Assert.Throws<FileNotFoundException>(() => PathPolicy.Check(p, roots, PathMode.Read));
        Assert.Equal($"File not found: '{Path.GetFullPath(p)}'.", ex.Message);
    }

    [Fact]
    public void Read_Folder_ThrowsFileNotFound()
    {
        var p = Path.Combine(Docs, "sub");
        Directory.CreateDirectory(p);
        var ex = Assert.Throws<FileNotFoundException>(() => PathPolicy.Check(p, roots, PathMode.Read));
        Assert.Equal($"'{Path.GetFullPath(p)}' is a folder, not a file.", ex.Message);
    }

    [Fact]
    public void Write_CreatesMissingFolders()
    {
        var p = Path.Combine(Docs, "x", "y", "z");
        var r = PathPolicy.Check(p, roots, PathMode.Write);
        Assert.Equal(Path.GetFullPath(p), r);
        Assert.True(Directory.Exists(r));
    }

    [Fact]
    public void Write_ExistingFolder_Ok()
    {
        var p = Path.Combine(Docs, "sub");
        Directory.CreateDirectory(p);
        Assert.Equal(Path.GetFullPath(p), PathPolicy.Check(p, roots, PathMode.Write));
    }

    [Fact]
    public void Write_PathIsFile_ThrowsIOException()
    {
        var f = Touch(Docs, "a.txt");
        var ex = Assert.Throws<IOException>(() => PathPolicy.Check(f, roots, PathMode.Write));
        Assert.Equal($"'{Path.GetFullPath(f)}' is a file, not a folder.", ex.Message);
    }

    [Fact]
    public void Write_Outside_DoesNotCreate()
    {
        var p = Path.Combine(T, "evil", "dir");
        Assert.Equal(PathRejectReason.OutsideAllowedFolders, Refused(p, roots, PathMode.Write));
        Assert.False(Directory.Exists(Path.Combine(T, "evil")));
    }

    [Fact]
    public void Message_NamesAllowedFolders()
    {
        var f = Touch(T, "outside.txt");
        var ex = Assert.Throws<PathNotAllowedException>(() => PathPolicy.Check(f, [Docs, Docs2], PathMode.Read));
        Assert.Equal(
            $"Path '{f}' is not allowed: it is outside the allowed folders. Allowed folders: {Path.GetFullPath(Docs)}; {Path.GetFullPath(Docs2)}.",
            ex.Message);
    }

    [Fact]
    public void Message_NoRoots()
    {
        var ex = Assert.Throws<PathNotAllowedException>(() => PathPolicy.Check(Path.Combine(Docs, "a.txt"), [], PathMode.Read));
        Assert.EndsWith("Allowed folders: (none).", ex.Message);
    }

    [Fact]
    public void DirectorySymlinkBelowRoot_Refused()
    {
        var outside = Path.Combine(T, "outsideDir");
        Touch(outside, "s.txt");
        SymlinkOrSkip(() => Directory.CreateSymbolicLink(Path.Combine(Docs, "link"), outside));
        Assert.Equal(PathRejectReason.LinkBelowRoot,
            Refused(Path.Combine(Docs, "link", "s.txt"), roots, PathMode.Read));
    }

    [Fact]
    public void FileSymlinkBelowRoot_Refused()
    {
        var outside = Touch(T, "outside.txt");
        SymlinkOrSkip(() => File.CreateSymbolicLink(Path.Combine(Docs, "l.txt"), outside));
        Assert.Equal(PathRejectReason.LinkBelowRoot,
            Refused(Path.Combine(Docs, "l.txt"), roots, PathMode.Read));
    }

    [Fact]
    public void DanglingSymlink_Write_Refused()
    {
        var target = Path.Combine(T, "nonexistent");
        SymlinkOrSkip(() => Directory.CreateSymbolicLink(Path.Combine(Docs, "dang"), target));
        Assert.Equal(PathRejectReason.LinkBelowRoot,
            Refused(Path.Combine(Docs, "dang", "sub"), roots, PathMode.Write));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void RootIsSymlink_Allowed()
    {
        Touch(Docs, "a.txt");
        var link = Path.Combine(T, "rootlink");
        SymlinkOrSkip(() => Directory.CreateSymbolicLink(link, Docs));
        var p = Path.Combine(link, "a.txt");
        Assert.Equal(Path.GetFullPath(p), PathPolicy.Check(p, [link], PathMode.Read));
    }

    [Theory]
    [InlineData(PathRejectReason.Empty, "the path is empty")]
    [InlineData(PathRejectReason.InvalidCharacters, "the path contains control characters")]
    [InlineData(PathRejectReason.DeviceOrUnc, "device and network (UNC) paths are not supported")]
    [InlineData(PathRejectReason.AlternateDataStream, "alternate data streams are not supported")]
    [InlineData(PathRejectReason.NotFullyQualified, @"the path must be absolute, like C:\Users\you\Documents\file.pdf")]
    [InlineData(PathRejectReason.ReservedDeviceName, "the path uses a reserved device name")]
    [InlineData(PathRejectReason.NoAllowedFolders, "no allowed folders are configured")]
    [InlineData(PathRejectReason.OutsideAllowedFolders, "it is outside the allowed folders")]
    [InlineData(PathRejectReason.LinkBelowRoot, "it goes through a junction or symbolic link")]
    [InlineData(PathRejectReason.Inaccessible, "part of the path could not be inspected")]
    public void Describe_AllReasonsHaveText(PathRejectReason reason, string text)
    {
        Assert.Equal(text, PathPolicy.Describe(reason));
    }

    // ---- Windows only ----

    [Fact]
    [Trait("os", "windows")]
    public void JunctionEscape_Refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var outside = Path.Combine(T, "outsideDir");
        Touch(outside, "s.txt");
        var link = Path.Combine(Docs, "j");
        var psi = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(outside);
        using (var proc = Process.Start(psi)!)
        {
            proc.WaitForExit();
            Assert.Equal(0, proc.ExitCode);
        }
        try
        {
        Assert.Equal(PathRejectReason.LinkBelowRoot, Refused(Path.Combine(link, "s.txt"), roots, PathMode.Read));
        Assert.Equal(PathRejectReason.LinkBelowRoot, Refused(Path.Combine(link, "newdir"), roots, PathMode.Write));
        Assert.False(Directory.Exists(Path.Combine(outside, "newdir")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    [Trait("os", "windows")]
    public void DriveRelativeAndRootRelative_Refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.Equal(PathRejectReason.NotFullyQualified, Refused("C:foo.txt", roots, PathMode.Read));
        Assert.Equal(PathRejectReason.NotFullyQualified, Refused(@"\foo.txt", roots, PathMode.Read));
    }

    [Fact]
    [Trait("os", "windows")]
    public void BackslashAndForwardSlashMixed_Normalized()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var f = Touch(Docs, "a.txt");
        var p = Docs.Replace('\\', '/') + "/a.txt";
        Assert.Equal(Path.GetFullPath(f), PathPolicy.Check(p, roots, PathMode.Read));
    }

    [Fact]
    [Trait("os", "windows")]
    public void TrailingDotsSpaces_Normalized()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.Equal(PathRejectReason.OutsideAllowedFolders, Refused(Docs2 + " .", roots, PathMode.Write));
        var f = Touch(Docs, "a.txt");
        Assert.Equal(Path.GetFullPath(f), PathPolicy.Check(f, [Docs + "."], PathMode.Read));
    }

    private static void MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
    }

    [Fact]
    [Trait("os", "windows")]
    public void JunctionWithTrailingDot_Refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var outside = Path.Combine(T, "outsideDot");
        Touch(outside, "s.txt");
        var link = Path.Combine(Docs, "link");
        MakeJunction(link, outside);
        try
        {
            Assert.Equal(PathRejectReason.LinkBelowRoot, Refused(Path.Combine(Docs, "link.", "s.txt"), roots, PathMode.Read));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    [Trait("os", "windows")]
    public void JunctionAsShortName_Refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var outside = Path.Combine(T, "outsideShort");
        Touch(outside, "s.txt");
        var link = Path.Combine(Docs, "linkjunction");
        MakeJunction(link, outside);
        try
        {
            var shortPath = Path.Combine(Docs, "LINKJU~1");
            Assert.SkipUnless(Directory.Exists(shortPath), "8.3 short names are disabled on this volume");
            Assert.Equal(PathRejectReason.LinkBelowRoot, Refused(Path.Combine(shortPath, "s.txt"), roots, PathMode.Read));
        }
        finally { Directory.Delete(link); }
    }
}
