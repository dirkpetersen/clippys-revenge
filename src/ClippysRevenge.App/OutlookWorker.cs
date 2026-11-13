using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>The one STA thread that owns all Outlook COM (SPEC 4).</summary>
internal sealed class OutlookWorker : ComWorker<Outlook.Application>
{
    public OutlookWorker(ActivityHub hub, ILogger logger)
        : base(
            new OutlookAttacher(),
            hub,
            new ComWorkerOptions
            {
                ConfigureThread = t => t.SetApartmentState(ApartmentState.STA),
                Logger = logger,
            })
    {
    }
}

/// <summary>Attach-only access to a running Outlook through the running object table.</summary>
internal sealed class OutlookAttacher : IComAttacher<Outlook.Application>
{
    private const string ProgId = "Outlook.Application";

    // DllImport on purpose: the object out-parameter needs built-in COM marshalling, which LibraryImport does not do.
#pragma warning disable SYSLIB1054
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    [DllImport("oleaut32.dll", ExactSpelling = true)]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppunk);
#pragma warning restore SYSLIB1054

    public Outlook.Application? TryAttach()
    {
        var hr = CLSIDFromProgID(ProgId, out var clsid);
        if (hr < 0)
        {
            return Fail(hr);
        }

        hr = GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
        if (hr < 0)
        {
            return Fail(hr);
        }

        if (obj is Outlook.Application app)
        {
            return app;
        }
        if (obj is not null && Marshal.IsComObject(obj))
        {
            Marshal.ReleaseComObject(obj);
        }
        throw new InvalidOperationException("The running object is not Outlook.Application.");
    }

    public void Probe(Outlook.Application app) => _ = app.Name;

    public void Release(Outlook.Application app)
    {
        try
        {
            Marshal.FinalReleaseComObject(app);
        }
        catch
        {
            // Release must never throw.
        }
    }

    private static Outlook.Application? Fail(int hr)
    {
        if (ComErrors.IsNotRunning(hr))
        {
            return null;
        }
        Marshal.ThrowExceptionForHR(hr);
        return null;
    }
}
