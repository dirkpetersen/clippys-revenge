using System.Reflection;

namespace ClippysRevenge.Core.Tests;

public class ScaffoldTests
{
    private static Assembly LoadCore() => Assembly.Load(new AssemblyName("ClippysRevenge.Core"));

    [Fact]
    public void CoreAssembly_LoadsAndSharesProductVersion()
    {
        var core = LoadCore();
        Assert.Equal(typeof(ScaffoldTests).Assembly.GetName().Version, core.GetName().Version);
    }

    [Fact]
    public void CoreAssembly_HasNoWindowsOrComReferences()
    {
        string[] forbidden =
        [
            "System.Windows.Forms",
            "Microsoft.Office.Interop",
            "Microsoft.Win32.Registry",
            "System.Security.Cryptography.ProtectedData",
            "Microsoft.WindowsDesktop",
        ];
        var refs = LoadCore().GetReferencedAssemblies();
        Assert.DoesNotContain(refs, r => forbidden.Any(f =>
            r.Name is not null && r.Name.StartsWith(f, StringComparison.Ordinal)));
    }
}
