using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>The facts seam for DraftWorker over <see cref="FactsFile"/>, reporting availability changes once (SPEC 25.2
/// "Visibility"). Texts hold no path content or facts.</summary>
public sealed class FactsReporter : IFactsSource
{
    private readonly FactsFile _file;
    private readonly ActivityHub _activity;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private FactsSource? _last;

    /// <exception cref="ArgumentNullException">file or activity is null.</exception>
    public FactsReporter(FactsFile file, ActivityHub activity, ILogger? logger = null)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _log = logger;
    }

    /// <summary>Reads the file, reports a change of its availability once, returns the body when it has content, else "".
    /// Never throws.</summary>
    public string ReadForPrompt()
    {
        try
        {
            var p = _file.ReadForPrompt();
            try
            {
                Report(p.Source);
            }
            catch (Exception)
            {
                // The hub or the logger failing must never stop drafting.
            }
            return p.Source == FactsSource.File ? p.Body : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private void Report(FactsSource source)
    {
        FactsSource? previous;
        lock (_lock)
        {
            previous = _last;
            _last = source;
        }

        string text;
        LogLevel level;
        if (source == FactsSource.None && previous != FactsSource.None)
        {
            text = FactsTexts.SourceNone;
            level = LogLevel.Warning;
        }
        else if (source != FactsSource.None && previous == FactsSource.None)
        {
            text = FactsTexts.SourceBack;
            level = LogLevel.Information;
        }
        else return;

        try { _activity.Event(ActivityKind.Ai, text); }
        catch (Exception) { /* swallowed */ }
        try { _log?.Log(level, text); }
        catch (Exception) { /* swallowed */ }
    }
}

/// <summary>Creates the facts template once, automatically (SPEC 25.2 "Template"). Thread-safe.</summary>
public sealed class FactsTemplateStarter
{
    private readonly FactsFile _file;
    private readonly Func<AiDraftingSettings> _settings;
    private readonly Func<CancellationToken, Task<string?>> _ownerName;
    private readonly ILogger? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _done;

    /// <param name="ownerName">The own mailbox owner's display name; null/blank when unknown. May throw; any
    /// exception except OperationCanceledException for ct counts as null.</param>
    /// <exception cref="ArgumentNullException">file, settings or ownerName is null.</exception>
    public FactsTemplateStarter(FactsFile file, Func<AiDraftingSettings> settings,
        Func<CancellationToken, Task<string?>> ownerName, ILogger? logger = null)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ownerName = ownerName ?? throw new ArgumentNullException(nameof(ownerName));
        _log = logger;
    }

    /// <summary>Throws only OperationCanceledException for ct.</summary>
    public async Task<FactsTemplateResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_done) return FactsTemplateResult.AlreadyDone;
        if (!_gate.Wait(0)) return FactsTemplateResult.Busy;
        try
        {
            bool consent;
            try { consent = _settings().HasConsent; }
            catch (Exception) { consent = false; }
            if (!consent) return FactsTemplateResult.NoConsent;

            if (_file.TemplateRecorded())
            {
                _done = true;
                return FactsTemplateResult.AlreadyDone;
            }

            string? name;
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
            if (string.IsNullOrWhiteSpace(name))
            {
                Log(LogLevel.Debug, "Facts about you: template waits until Outlook is available.");
                return FactsTemplateResult.NameUnknown;
            }

            var o = _file.CreateTemplateOnce(name);
            switch (o.Result)
            {
                case FactsTemplateResult.Created:
                    Log(LogLevel.Information, "Facts about you: template created.");
                    _done = true;
                    break;
                case FactsTemplateResult.AlreadyExists:
                    Log(LogLevel.Debug, "Facts about you: file already exists, no template needed.");
                    _done = true;
                    break;
                case FactsTemplateResult.AlreadyDone:
                    _done = true;
                    break;
                case FactsTemplateResult.Failed:
                    Log(LogLevel.Warning, "Facts about you: couldn't create the template (" + o.ErrorType + ")");
                    break;
            }
            return o.Result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fire-and-forget RunAsync on the thread pool; returns at once when a previous run finished the job.
    /// Never throws.</summary>
    public void Kick()
    {
        try
        {
            if (_done) return;
            _ = Task.Run(async () =>
            {
                try { await RunAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* swallowed */ }
            });
        }
        catch (Exception)
        {
            // Never throws.
        }
    }

    private void Log(LogLevel level, string text)
    {
        try { _log?.Log(level, text); }
        catch (Exception) { /* swallowed */ }
    }
}
