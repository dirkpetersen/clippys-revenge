using System.Security.Cryptography;
using ClippysRevenge.Core;

namespace ClippysRevenge.App;

/// <summary>DPAPI, current user scope (SPEC 5.1).</summary>
internal sealed class DpapiProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) =>
        ProtectedData.Unprotect(protectedData, null, DataProtectionScope.CurrentUser);
}
