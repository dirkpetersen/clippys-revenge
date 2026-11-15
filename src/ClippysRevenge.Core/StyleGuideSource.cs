using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>
/// Adapts M11.6's <see cref="StyleSkill"/> to the M11.7 drafting seam <see cref="IStyleGuideSource"/> (SPEC 19.6
/// "Use in drafting"). Before each automatic pass it runs the "no skill yet" / weekly generation check for the
/// own mailbox's owner; reading the style text never waits on a running generation. When the file the style text
/// comes from changes (user file, Clippy's copy, none), it says so once (SPEC 19.6 "Visible style source").
/// </summary>
public sealed class StyleGuideSource : IStyleGuideSource
{
    private readonly StyleSkill _skill;
    private readonly Func<CancellationToken, Task<Recipient?>> _owner;
    private readonly ActivityHub _activity;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private StyleSource? _lastReported;

    /// <param name="skill">The writing-style skill service.</param>
    /// <param name="owner">Resolves the own mailbox's owner (name and SMTP address); null when unknown.</param>
    /// <param name="activity">Receives the source-change events.</param>
    /// <exception cref="ArgumentNullException">skill, owner or activity is null.</exception>
    public StyleGuideSource(StyleSkill skill, Func<CancellationToken, Task<Recipient?>> owner,
        ActivityHub activity, ILogger? logger = null)
    {
        _skill = skill ?? throw new ArgumentNullException(nameof(skill));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _log = logger;
    }

    /// <summary>Runs <see cref="StyleSkill.RunIfDueAsync"/> when the owner is known and has a valid address.
    /// Throws only OperationCanceledException for <paramref name="ct"/>; anything else is swallowed
    /// (StyleSkill records its own events, and a style failure must never stop drafting).</summary>
    public async Task BeforePassAsync(CancellationToken ct)
    {
        try
        {
            var owner = await _owner(ct).ConfigureAwait(false);
            if (owner is null || !EmailAddress.IsValid(owner.Email)) return;
            await _skill.RunIfDueAsync(owner, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Swallowed by design; see the summary.
        }
    }

    /// <summary>The style text for the prompt; reports a change of its source once. Never throws.</summary>
    public string ReadForPrompt()
    {
        var p = _skill.ReadForPrompt();
        try
        {
            Report(p.Source);
        }
        catch (Exception)
        {
            // The hub or the logger failing must never stop drafting.
        }
        return p.Body;
    }

    private void Report(StyleSource source)
    {
        StyleSource? previous;
        lock (_lock)
        {
            previous = _lastReported;
            if (previous == source) return;
            _lastReported = source;
        }

        string? text;
        LogLevel level;
        switch (source)
        {
            case StyleSource.None:
                text = StyleTexts.SourceNone;
                level = LogLevel.Warning;
                break;
            case StyleSource.AppCopy:
                text = StyleTexts.SourceAppCopy;
                level = LogLevel.Warning;
                break;
            default:
                // First read after start-up with the user file: nothing to say.
                if (previous is null) return;
                text = StyleTexts.SourceBack;
                level = LogLevel.Information;
                break;
        }

        try { _activity.Event(ActivityKind.Ai, text); }
        catch (Exception) { /* swallowed */ }
        try { _log?.Log(level, text); }
        catch (Exception) { /* swallowed */ }
    }
}
