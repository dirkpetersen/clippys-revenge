namespace ClippysRevenge.Core;

/// <summary>What "Edit my facts…" opens: Path, or Error (a reason for <see cref="AiTabTexts.OpenFailed"/>). Exactly one is set.</summary>
public sealed record FactsEditTarget(string? Path, string? Error);

/// <summary>"Edit my facts…" (SPEC 25.2): creates the template when the file is missing, then names the file to open.
/// Consent isn't needed: nothing is sent. Holds no facts and logs nothing.</summary>
public sealed class FactsEditor
{
    private readonly FactsFile _file;
    private readonly Func<CancellationToken, Task<string?>>? _ownerName;

    /// <param name="ownerName">The own mailbox owner's display name, or null/blank when unknown. May throw; any exception
    /// except OperationCanceledException for ct counts as unknown. Null: always unknown.</param>
    /// <exception cref="ArgumentNullException">file is null.</exception>
    public FactsEditor(FactsFile file, Func<CancellationToken, Task<string?>>? ownerName = null)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _ownerName = ownerName;
    }

    public FactsFile File => _file;

    /// <summary>Throws only OperationCanceledException for ct. A missing file is created from the template (an unknown
    /// name becomes "the user"); a failure gives Error = "couldn't create the file (&lt;exception type&gt;)".</summary>
    public async Task<FactsEditTarget> PrepareAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = _file.Paths.File;
        try
        {
            if (System.IO.File.Exists(path)) return new FactsEditTarget(path, null);
        }
        catch (Exception)
        {
            // Fall through: CreateIfMissing reports the real problem.
        }

        string? name = null;
        if (_ownerName is not null)
        {
            try
            {
                name = await _ownerName(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                name = null;
            }
        }

        var o = _file.CreateIfMissing(name);
        return o.Result is FactsTemplateResult.Created or FactsTemplateResult.AlreadyExists
            ? new FactsEditTarget(path, null)
            : new FactsEditTarget(null, "couldn't create the file (" + (o.ErrorType ?? "unknown error") + ")");
    }

    /// <summary>The Settings status line for the file as it is now. Never throws.</summary>
    public string StatusText() => FactsTexts.Status(_file.ReadForPrompt().Source);
}
