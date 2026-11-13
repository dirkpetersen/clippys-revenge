using System.Security.Cryptography;

namespace ClippysRevenge.Core;

public static class AuthToken
{
    /// <summary>32 random bytes as 64 lower-case hex characters (SPEC 5.2).</summary>
    public static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
