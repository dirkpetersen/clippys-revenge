using ClippysRevenge.Core;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class AiStatusCheckerTests
{
    private sealed class Model(List<string> order) : IDraftModel
    {
        public Func<CancellationToken, Task<ModelStatus>> Check { get; set; } = _ => Task.FromResult(new ModelStatus(true, "✓ ready"));

        public async Task<ModelStatus> CheckAsync(CancellationToken ct)
        {
            order.Add("check");
            return await Check(ct);
        }

        public Task<ModelResult> RunAsync(ModelRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private readonly List<string> _order = [];
    private readonly List<ModelStatus> _reported = [];
    private readonly CapturingLog _log = new();

    private AiStatusChecker Make(Model m) =>
        new(m, () => _order.Add("invalidate"), s => { _order.Add("report"); _reported.Add(s); }, _log);

    [Fact]
    public async Task Refresh_invalidates_first()
    {
        var m = new Model(_order);
        var s = await Make(m).CheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(["invalidate", "check", "report"], _order);
        Assert.Equal(new ModelStatus(true, "✓ ready"), s);
        Assert.Equal(s, Assert.Single(_reported));
    }

    [Fact]
    public async Task No_refresh_skips_invalidate_but_reports()
    {
        await Make(new Model(_order)).CheckAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(["check", "report"], _order);
    }

    [Fact]
    public async Task A_throwing_model_gives_the_failure_status_and_logs_only_the_type()
    {
        var m = new Model(_order) { Check = _ => throw new InvalidOperationException("SECRET-STATUS") };
        var s = await Make(m).CheckAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(new ModelStatus(false, AiTabTexts.CheckFailed), s);
        Assert.Contains(_log.Lines, l => l.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("SECRET-STATUS", StringComparison.Ordinal));
        Assert.Equal(s, Assert.Single(_reported));
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        var m = new Model(_order) { Check = ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(new ModelStatus(true, "")); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make(m).CheckAsync(false, cts.Token));
    }

    [Fact]
    public async Task A_throwing_report_does_not_change_the_result()
    {
        var checker = new AiStatusChecker(new Model(_order), null, _ => throw new IOException("x"), _log);
        var s = await checker.CheckAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(new ModelStatus(true, "✓ ready"), s);
    }

    [Fact]
    public async Task Null_callbacks_work()
    {
        var s = await new AiStatusChecker(new Model(_order), null, null).CheckAsync(true, TestContext.Current.CancellationToken);
        Assert.True(s.Ready);
        Assert.Throws<ArgumentNullException>(() => new AiStatusChecker(null!, null, null));
    }
}