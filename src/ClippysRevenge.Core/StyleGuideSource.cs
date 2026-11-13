namespace ClippysRevenge.Core;

/// <summary>
/// Adapts M11.6's <see cref="StyleSkill"/> to the M11.7 drafting seam <see cref="IStyleGuideSource"/> (SPEC 19.6
/// "Use in drafting"). Before each automatic pass it runs the "no skill yet" / weekly generation check for the
/// own mailbox's owner; reading the style text never waits on a running generation.
/// </summary>
public sealed class StyleGuideSource : IStyleGuideSource
{
    private readonly StyleSkill _skill;
    private readonly Func<CancellationToken, Task<Recipient?>> _owner;

    /// <param name="skill">The writing-style skill service.</param>
    /// <param name="owner">Resolves the own mailbox's owner (name and SMTP address); null when unknown.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public StyleGuideSource(StyleSkill skill, Func<CancellationToken, Task<Recipient?>> owner)
    {
        _skill = skill ?? throw new ArgumentNullException(nameof(skill));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
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

    public string ReadForPrompt() => _skill.ReadBodyForPrompt();
}
