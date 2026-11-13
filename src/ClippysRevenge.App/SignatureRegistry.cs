using System.Security;
using ClippysRevenge.Core;
using Microsoft.Win32;

namespace ClippysRevenge.App;

internal static class SignatureRegistry
{
    internal const string KeyPath = @"Software\Microsoft\Office\16.0\Common\MailSettings";

    /// <summary>HKCU value "NewSignature" or "ReplySignature" as a signature name, or null.</summary>
    public static string? Name(SignatureKind kind)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            var value = key?.GetValue(kind == SignatureKind.Reply ? "ReplySignature" : "NewSignature");
            return Signatures.NameFromRegistryValue(value);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
