using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ClippysRevenge.App;
using Xunit;

namespace ClippysRevenge.Core.Tests;

/// <summary>Windows only; no Outlook (SPEC 24.2). Not a live test.</summary>
[Trait("os", "windows")]
public sealed class PrivateFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-private-" + Guid.NewGuid().ToString("N"));

    public PrivateFolderTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_root))
                if (new DirectoryInfo(d).LinkTarget is not null) Directory.Delete(d);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static SecurityIdentifier Me => WindowsIdentity.GetCurrent().User!;

    private static void AssertOnlyMe(FileSystemSecurity acl, bool directory)
    {
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.NotEmpty(rules);
        Assert.True(acl.AreAccessRulesProtected || !directory);
        var rights = (FileSystemRights)0;
        var inheritance = InheritanceFlags.None;
        foreach (var r in rules)
        {
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.Equal(Me, r.IdentityReference);
            if (directory) Assert.False(r.IsInherited);
            rights |= r.FileSystemRights;
            inheritance |= r.InheritanceFlags;
        }
        Assert.Equal(FileSystemRights.FullControl, rights & FileSystemRights.FullControl);
        if (directory) Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, inheritance);
    }

    private static void AssertUserOnly(string folder) =>
        AssertOnlyMe(new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access), directory: true);

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
    public void MissingFolder_CreatedWithParents()
    {
        var f = Path.Combine(_root, "a", "b", "links");
        PrivateFolder.Ensure(f);
        Assert.True(Directory.Exists(f));
        AssertUserOnly(f);
    }

    [Fact]
    public void ExistingFolder_IsFixed()
    {
        var f = Path.Combine(_root, "links");
        Directory.CreateDirectory(f);
        PrivateFolder.Ensure(f);
        AssertUserOnly(f);
    }

    [Fact]
    public void Idempotent()
    {
        var f = Path.Combine(_root, "links");
        PrivateFolder.Ensure(f);
        PrivateFolder.Ensure(f);
        AssertUserOnly(f);
    }

    [Fact]
    public void FileInside_InheritsUserOnly()
    {
        var f = Path.Combine(_root, "links");
        PrivateFolder.Ensure(f);
        var file = Path.Combine(f, "x.bin");
        File.WriteAllText(file, "x");
        AssertOnlyMe(new FileInfo(file).GetAccessControl(AccessControlSections.Access), directory: false);
    }

    [Fact]
    public void Junction_Refused_TargetUntouched()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var before = new DirectoryInfo(target).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var j = Path.Combine(_root, "j");
        MakeJunction(j, target);
        var ex = Assert.Throws<IOException>(() => PrivateFolder.Ensure(j));
        Assert.DoesNotContain(_root, ex.Message);
        var after = new DirectoryInfo(target).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Symlink_Refused()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "s"), target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("symlink creation not permitted");
        }
        Assert.Throws<IOException>(() => PrivateFolder.Ensure(Path.Combine(_root, "s")));
    }

    [Fact]
    public void FileAtPath_Refused()
    {
        var p = Path.Combine(_root, "afile");
        File.WriteAllText(p, "x");
        var ex = Assert.Throws<IOException>(() => PrivateFolder.Ensure(p));
        Assert.DoesNotContain(_root, ex.Message);
    }

    [Theory]
    [InlineData("rel\\links")]
    [InlineData("")]
    [InlineData("  ")]
    public void BadArguments(string folder)
    {
        Assert.ThrowsAny<ArgumentException>(() => PrivateFolder.Ensure(folder));
        Assert.False(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "rel")));
    }

    [Fact]
    public void WithAttachmentLinks_RefusesJunctionAndKeepsFilesOutside()
    {
        var f = Path.Combine(_root, "links");
        using var links = new AttachmentLinks(new AttachmentLinksOptions { Folder = f, EnsurePrivateFolder = PrivateFolder.Ensure });
        var temp = links.PrepareTempPath();
        Assert.Equal(f, Path.GetDirectoryName(temp));
        AssertUserOnly(f);

        Directory.Delete(f, recursive: true);
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var bait = Path.Combine(target, "0123456789abcdef0123456789abcdef.bin");
        File.WriteAllText(bait, "x");
        MakeJunction(f, target);

        var ex = Assert.Throws<AttachmentLinkException>(() => links.PrepareTempPath());
        Assert.Equal(LinkRefusal.FolderUnusable, ex.Reason);
        links.CleanUpAtStart();
        Assert.True(File.Exists(bait));
    }
}
