using System.Runtime.InteropServices;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

public enum WorkPriority { Interactive, Background }

public enum OutlookConnectionState { Unknown, Available, Unavailable, NotResponding }

/// <summary>How the worker reaches the COM server. Every member is called on the worker thread only.</summary>
public interface IComAttacher<TApp> where TApp : class
{
    /// <summary>
    /// Returns the running instance, or null when none is running / not installed.
    /// Throws for any other failure (the worker treats a throw like null and logs it at Warning).
    /// Must never start the server.
    /// </summary>
    TApp? TryAttach();

    /// <summary>One cheap cross-process call. Throws (COMException) when the server is gone.</summary>
    void Probe(TApp app);

    /// <summary>Releases the instance. Must not throw; the worker also catches.</summary>
    void Release(TApp app);
}

/// <summary>Doubling delay between attach attempts. Not thread-safe (worker thread only).</summary>
public sealed class AttachBackoff
{
    public static readonly TimeSpan DefaultInitial = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultMax = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private TimeSpan _current;

    public AttachBackoff(TimeSpan? initial = null, TimeSpan? max = null)
    {
        _initial = initial ?? DefaultInitial;
        _max = max ?? DefaultMax;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_initial, TimeSpan.Zero, nameof(initial));
        ArgumentOutOfRangeException.ThrowIfLessThan(_max, _initial, nameof(max));
        _current = _initial;
    }

    public TimeSpan Next()
    {
        var result = _current;
        var doubled = _current + _current;
        _current = doubled > _max ? _max : doubled;
        return result;
    }

    public void Reset() => _current = _initial;
}

public static class OutlookConnection
{
    /// <summary>ActivityHub label of the OutlookUnavailable scope while NotResponding.</summary>
    public const string NotRespondingLabel = "not responding";
}

public static class ComErrors
{
    public const int MK_E_UNAVAILABLE = unchecked((int)0x800401E3);
    public const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
    public const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);
    public const int RPC_S_SERVER_UNAVAILABLE = unchecked((int)0x800706BA);
    public const int RPC_S_CALL_FAILED = unchecked((int)0x800706BE);
    public const int CO_E_CLASSSTRING = unchecked((int)0x800401F3);
    public const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
    public const int MAPI_E_NOT_FOUND = unchecked((int)0x8004010F);
    public const int MAPI_E_INVALID_ENTRYID = unchecked((int)0x80040107);

    /// <summary>True when ex itself is a COMException with MAPI_E_NOT_FOUND or MAPI_E_INVALID_ENTRYID
    /// (GetItemFromID on an item that no longer exists). Inner exceptions are not inspected.</summary>
    public static bool IsItemNotFound(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex is System.Runtime.InteropServices.COMException c && c.HResult is MAPI_E_NOT_FOUND or MAPI_E_INVALID_ENTRYID;
    }

    public static bool IsGoneHResult(int hr) =>
        hr is MK_E_UNAVAILABLE or RPC_E_DISCONNECTED or RPC_E_SERVERCALL_RETRYLATER or RPC_S_SERVER_UNAVAILABLE or RPC_S_CALL_FAILED;

    public static bool IsOutlookGone(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is COMException c && IsGoneHResult(c.HResult))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsNotRunning(int hr) =>
        hr is MK_E_UNAVAILABLE or CO_E_CLASSSTRING or REGDB_E_CLASSNOTREG;
}

/// <summary>Thrown for work that can't run because Outlook isn't attached (SPEC 4).</summary>
public class OutlookUnavailableException : McpException
{
    public const string DefaultMessage =
        "Outlook is not running. Ask the user to start Outlook (tray → Start Outlook) and try again.";

    public OutlookUnavailableException() : base(DefaultMessage)
    {
    }

    public OutlookUnavailableException(Exception? inner) : base(DefaultMessage, inner)
    {
    }

    protected OutlookUnavailableException(string message, Exception? inner) : base(message, inner)
    {
    }
}

/// <summary>A COM call missed its deadline, or the worker is NotResponding (SPEC 4).</summary>
public sealed class OutlookNotRespondingException : OutlookUnavailableException
{
    public new const string DefaultMessage =
        "Outlook isn't responding. It may be busy, offline or showing a dialog box. Ask the user to check Outlook, then try again. If this call was creating a draft or a meeting, check Outlook for it before retrying.";

    public OutlookNotRespondingException() : base(DefaultMessage, null)
    {
    }

    public OutlookNotRespondingException(Exception? inner) : base(DefaultMessage, inner)
    {
    }
}

public static class OutlookAppPath
{
    private const string ExeName = "OUTLOOK.EXE";

    /// <summary>App Paths value to a usable exe path, or null. Never throws; no file I/O.</summary>
    public static string? Normalize(string? registryValue)
    {
        if (string.IsNullOrWhiteSpace(registryValue))
        {
            return null;
        }
        var s = registryValue.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            s = s[1..^1].Trim();
        }
        foreach (var c in s)
        {
            if (c < 0x20 || "\"<>|*?/".Contains(c))
            {
                return null;
            }
        }
        if (s.Length < 3 || !(s[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z') || s[1] != ':' || s[2] != '\\')
        {
            return null;
        }
        var name = s[(s.LastIndexOf('\\') + 1)..];
        return string.Equals(name, ExeName, StringComparison.OrdinalIgnoreCase) ? s : null;
    }
}
