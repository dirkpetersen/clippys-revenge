using System.Runtime.InteropServices;
using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class OutlookConnectionTests
{
    [Fact]
    public void BackoffDefault()
    {
        var b = new AttachBackoff();
        var seen = Enumerable.Range(0, 10).Select(_ => (int)b.Next().TotalSeconds).ToArray();
        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60, 60, 60], seen);
        b.Reset();
        Assert.Equal(TimeSpan.FromSeconds(1), b.Next());
    }

    [Fact]
    public void BackoffCustom()
    {
        var b = new AttachBackoff(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40));
        Assert.Equal([10, 20, 40, 40], Enumerable.Range(0, 4).Select(_ => (int)b.Next().TotalMilliseconds).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AttachBackoff(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AttachBackoff(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AttachBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
        var c = new AttachBackoff(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), c.Next());
        Assert.Equal(TimeSpan.FromSeconds(3), c.Next());
    }

    [Theory]
    [InlineData(0x800401E3, true)]
    [InlineData(0x80010108, true)]
    [InlineData(0x8001010A, true)]
    [InlineData(0x800706BA, true)]
    [InlineData(0, false)]
    [InlineData(0x80004005, false)]
    [InlineData(0x80010001, false)]
    [InlineData(0x800401F3, false)]
    public void IsGoneHResult(uint hr, bool expected) =>
        Assert.Equal(expected, ComErrors.IsGoneHResult(unchecked((int)hr)));

    [Fact]
    public void IsOutlookGone()
    {
        var gone = new COMException("x", ComErrors.RPC_E_DISCONNECTED);
        Assert.True(ComErrors.IsOutlookGone(gone));
        Assert.True(ComErrors.IsOutlookGone(new Exception("a", new Exception("b", gone))));
        Assert.True(ComErrors.IsOutlookGone(new AggregateException(gone)));
        Assert.False(ComErrors.IsOutlookGone(new Exception("x") { HResult = ComErrors.RPC_E_DISCONNECTED }));
        Assert.False(ComErrors.IsOutlookGone(new COMException("x", unchecked((int)0x80004005))));
        Assert.Throws<ArgumentNullException>(() => ComErrors.IsOutlookGone(null!));
    }

    [Fact]
    public void IsItemNotFound()
    {
        Assert.True(ComErrors.IsItemNotFound(new COMException("x", unchecked((int)0x8004010F))));
        Assert.True(ComErrors.IsItemNotFound(new COMException("x", unchecked((int)0x80040107))));
        Assert.False(ComErrors.IsItemNotFound(new COMException("x", unchecked((int)0x80004005))));
        Assert.False(ComErrors.IsItemNotFound(new COMException("x", ComErrors.RPC_E_DISCONNECTED)));
        Assert.False(ComErrors.IsItemNotFound(new InvalidOperationException("x", new COMException("x", unchecked((int)0x8004010F)))));
        Assert.Throws<ArgumentNullException>(() => ComErrors.IsItemNotFound(null!));
    }

    [Fact]
    public void IsNotRunning()
    {
        Assert.True(ComErrors.IsNotRunning(ComErrors.MK_E_UNAVAILABLE));
        Assert.True(ComErrors.IsNotRunning(ComErrors.CO_E_CLASSSTRING));
        Assert.True(ComErrors.IsNotRunning(ComErrors.REGDB_E_CLASSNOTREG));
        Assert.False(ComErrors.IsNotRunning(ComErrors.RPC_E_DISCONNECTED));
        Assert.False(ComErrors.IsNotRunning(unchecked((int)0x80070005)));
    }

    [Fact]
    public void Constants()
    {
        Assert.Equal(unchecked((int)0x800401E3), ComErrors.MK_E_UNAVAILABLE);
        Assert.Equal(unchecked((int)0x80010108), ComErrors.RPC_E_DISCONNECTED);
        Assert.Equal(unchecked((int)0x8001010A), ComErrors.RPC_E_SERVERCALL_RETRYLATER);
        Assert.Equal(unchecked((int)0x800706BA), ComErrors.RPC_S_SERVER_UNAVAILABLE);
        Assert.Equal(unchecked((int)0x800401F3), ComErrors.CO_E_CLASSSTRING);
        Assert.Equal(unchecked((int)0x80040154), ComErrors.REGDB_E_CLASSNOTREG);
    }

    [Fact]
    public void UnavailableException()
    {
        var e = new OutlookUnavailableException();
        Assert.Equal("Outlook is not running. Ask the user to start Outlook (tray → Start Outlook) and try again.", e.Message);
        Assert.IsAssignableFrom<McpException>(e);
        var inner = new InvalidOperationException("i");
        Assert.Same(inner, new OutlookUnavailableException(inner).InnerException);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE", @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE")]
    [InlineData("\"C:\\Program Files\\Microsoft Office\\root\\Office16\\OUTLOOK.EXE\"", @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE")]
    [InlineData(@"  C:\Office\outlook.exe  ", @"C:\Office\outlook.exe")]
    [InlineData("\" C:\\Office\\Outlook.Exe \"", @"C:\Office\Outlook.Exe")]
    [InlineData(@"d:\x\OUTLOOK.EXE", @"d:\x\OUTLOOK.EXE")]
    [InlineData(@"C:\Office\notepad.exe", null)]
    [InlineData(@"C:\Office\OUTLOOK.EXE.bak", null)]
    [InlineData(@"C:\Office\xOUTLOOK.EXE", null)]
    [InlineData("OUTLOOK.EXE", null)]
    [InlineData(@"\\server\share\OUTLOOK.EXE", null)]
    [InlineData(@"\\?\C:\Office\OUTLOOK.EXE", null)]
    [InlineData("C:/Office/OUTLOOK.EXE", null)]
    [InlineData("C:\\Off\"ice\\OUTLOOK.EXE", null)]
    [InlineData(@"C:\Off|ice\OUTLOOK.EXE", null)]
    [InlineData("\"C:\\Office\\OUTLOOK.EXE", null)]
    [InlineData(@"1:\OUTLOOK.EXE", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void NormalizeAppPath(string? input, string? expected) =>
        Assert.Equal(expected, OutlookAppPath.Normalize(input));

    [Fact]
    public void CallFailedCountsAsGone()
    {
        Assert.True(ComErrors.IsGoneHResult(unchecked((int)0x800706BE)));
        Assert.True(ComErrors.IsOutlookGone(new COMException("x", unchecked((int)0x800706BE))));
    }

    [Fact]
    public void NotRespondingException()
    {
        var ex = new OutlookNotRespondingException();
        Assert.IsAssignableFrom<McpException>(ex);
        Assert.IsAssignableFrom<OutlookUnavailableException>(ex);
        Assert.Equal(OutlookNotRespondingException.DefaultMessage, ex.Message);
        Assert.Equal("Outlook is not running. Ask the user to start Outlook (tray → Start Outlook) and try again.", OutlookUnavailableException.DefaultMessage);
    }

    [Fact]
    public void OutlookNotFoundText() =>
        Assert.Equal("It looks like I can't find Outlook on this PC. Start it from the Start menu.", BalloonTexts.OutlookNotFound);
}
