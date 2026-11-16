namespace ClippysRevenge.Core.Tests;

internal sealed class FakeFacts : IFactsSource
{
    private int _reads;

    public string Text { get; set; } = "";
    public int Reads => Volatile.Read(ref _reads);

    public string ReadForPrompt()
    {
        Interlocked.Increment(ref _reads);
        return Text;
    }
}
