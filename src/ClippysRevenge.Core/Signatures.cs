using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

public enum SignatureKind { New, Reply }

/// <summary>Default signature loader (SPEC section 8): name source, then the .htm file, decoded.</summary>
public sealed partial class Signatures
{
    public const string FileExtension = ".htm";
    public const int MaxFileBytes = 1_048_576;
    public const int MaxNameLength = 200;

    private static readonly char[] BadNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    [GeneratedRegex(@"charset\s*=\s*[""']?([A-Za-z0-9_.:\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CharsetDecl();

    private readonly Func<SignatureKind, string?> _nameSource;
    private readonly string _dir;

    static Signatures()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public Signatures(Func<SignatureKind, string?> nameSource, string signaturesDir)
    {
        ArgumentNullException.ThrowIfNull(nameSource);
        ArgumentNullException.ThrowIfNull(signaturesDir);
        if (!Path.IsPathFullyQualified(signaturesDir))
            throw new ArgumentException("signaturesDir must be fully qualified.", nameof(signaturesDir));
        _nameSource = nameSource;
        _dir = signaturesDir;
    }

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Signatures");

    public string? Default(SignatureKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        string? raw;
        try
        {
            raw = _nameSource(kind);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
        var name = raw?.Trim();
        if (!IsSafeName(name)) return null;

        var path = Path.Combine(_dir, name + FileExtension);
        try
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaxFileBytes) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxFileBytes) return null;
            return Decode(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength) return false;
        if (name is "." or "..") return false;
        foreach (var c in name)
            if (c < 0x20 || Array.IndexOf(BadNameChars, c) >= 0) return false;
        return true;
    }

    public static string? NameFromRegistryValue(object? value)
    {
        string? s = value switch
        {
            string str => str,
            byte[] bytes => Encoding.Unicode.GetString(bytes),
            _ => null,
        };
        if (s is null) return null;
        if (value is byte[])
        {
            var nul = s.IndexOf('\0');
            if (nul >= 0) s = s[..nul];
        }
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    internal static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(4096, bytes.Length));
        var m = CharsetDecl().Match(head);
        if (m.Success)
        {
            try
            {
                var enc = Encoding.GetEncoding(m.Groups[1].Value);
                if (enc.CodePage is not (1200 or 1201 or 12000 or 12001))
                    return enc.GetString(bytes);
            }
            catch (ArgumentException)
            {
                // unknown charset: fall through
            }
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }
}
