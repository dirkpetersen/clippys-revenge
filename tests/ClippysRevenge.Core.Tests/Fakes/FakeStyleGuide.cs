namespace ClippysRevenge.Core.Tests;

internal sealed class FakeStyleGuide : IStyleGuideSource
{
    private int _before;

    public Exception? ThrowBefore { get; set; }
    public int BeforeCalls => Volatile.Read(ref _before);

    public Task BeforePassAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _before);
        if (ThrowBefore is not null) throw ThrowBefore;
        return Task.CompletedTask;
    }

    public string ReadForPrompt() => "STYLE";
}
