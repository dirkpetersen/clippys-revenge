using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>
/// A service that may be missing because its backing store failed to open (for example the email index).
/// Tools take this as a DI parameter so they stay listed and callable; <see cref="Required"/> turns a missing
/// service into an actionable McpException.
/// </summary>
public sealed class Optional<T>(T? value) where T : class
{
    public const string IndexUnavailableMessage =
        "The email index could not be opened; see the tray (Error state). Restart Clippy or use Rebuild index.";

    public T? Value { get; } = value;

    public T Required() => Value ?? throw new McpException(IndexUnavailableMessage);
}
