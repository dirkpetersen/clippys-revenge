namespace ClippysRevenge.Core.Tests;

internal sealed class FakeStyleGuide : IStyleGuideSource
{
    private int _before;

    public Exception? ThrowBefore { get; set; }
    public int BeforeCalls => Volatile.Read(ref _before);

    /// <summary>When set, BeforePassAsync waits for it (after counting the call).</summary>
    public TaskCompletionSource? Block { get; set; }

    public Task BeforePassAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _before);
        if (ThrowBefore is not null) throw ThrowBefore;
        return Block?.Task ?? Task.CompletedTask;
    }

    public string ReadForPrompt() => "STYLE";
}
