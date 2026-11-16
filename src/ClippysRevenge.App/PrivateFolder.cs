using System.Security.AccessControl;
using System.Security.Principal;

namespace ClippysRevenge.App;

/// <summary>A folder only the current user can open (SPEC 24.2; reused by M19.7 for ai-files\). Windows only.</summary>
internal static class PrivateFolder
{
    /// <summary>Creates the folder (and missing parents) or reuses it, then sets a protected DACL with one Allow rule:
    /// current user SID, FullControl, ContainerInherit|ObjectInherit, PropagationFlags.None. Verifies it by reading it
    /// back. Idempotent. Exception messages never contain the path.</summary>
    /// <exception cref="ArgumentException">folder null/blank or not fully qualified.</exception>
    /// <exception cref="IOException">a file exists at the path, or the folder is a junction/symlink/reparse point.</exception>
    /// <exception cref="UnauthorizedAccessException">the DACL can't be set or doesn't read back as required.</exception>
    public static void Ensure(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Path.IsPathFullyQualified(folder))
            throw new ArgumentException("folder must be a fully qualified path.", nameof(folder));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

        var user = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("The current user has no SID.");

        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));

        if (File.Exists(full))
            throw new IOException("The private folder path is a file.");

        var info = new DirectoryInfo(full);
        if (!info.Exists)
        {
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            try
            {
                info.Create(sec);
            }
            catch (IOException) when (Directory.Exists(full))
            {
                // Appeared meanwhile: the checks below still run.
            }
        }

        RefuseLink(info);
        info.SetAccessControl(sec);

        var acl = info.GetAccessControl(AccessControlSections.Access);
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var rights = (FileSystemRights)0;
        var inheritance = InheritanceFlags.None;
        var ok = acl.AreAccessRulesProtected && rules.Count > 0;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IsInherited || rule.IdentityReference != user)
            {
                ok = false;
                break;
            }
            rights |= rule.FileSystemRights;
            inheritance |= rule.InheritanceFlags;
        }
        if (!ok
            || (rights & FileSystemRights.FullControl) != FileSystemRights.FullControl
            || inheritance != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
            throw new UnauthorizedAccessException("The private folder's access list could not be set.");

        RefuseLink(info);
    }

    private static void RefuseLink(DirectoryInfo info)
    {
        info.Refresh();
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The private folder is a junction or symbolic link.");
    }
}
