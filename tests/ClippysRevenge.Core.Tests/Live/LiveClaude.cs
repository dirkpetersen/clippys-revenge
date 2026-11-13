using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// Gate, model choice and helpers of the live Claude tests (M11.10, SPEC 19.12). The tests run on Haiku with
/// effort low, never on the app default. Nothing here reads the mail text of a Claude request or response.
/// </summary>
internal static class LiveClaude
{
    public const string DefaultModel = "claude-haiku-5-5";
    public const string Effort = "low";
    public const string ModelVariable = "CLIPPY_LIVE_CLAUDE_MODEL";   // optional override, in case the Haiku alias changes
    public const string OkSchema =
        """{"type":"object","additionalProperties":false,"required":["ok"],"properties":{"ok":{"type":"boolean"}}}""";

    public const string NotEnabled =
        "Set CLIPPY_LIVE=1 and CLIPPY_LIVE_CLAUDE=1 to run the live Claude tests (scripts/run-live-tests.ps1 -Claude does this). They use a little of your Claude usage.";
    public const string BadModelVariable = "CLIPPY_LIVE_CLAUDE_MODEL is not a valid model name; unset it to use claude-haiku-5-5.";
    public static string NotReady(string statusText) => "Claude Code is not ready on this PC: " + statusText;
    public const string UsageLimit = "Claude's usage limit was reached. Run the live Claude tests again later.";

    private static readonly Regex VersionRx =
        new(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Pure. True only when isWindows and both variables are exactly "1".</summary>
    public static bool IsEnabled(Func<string, string?> env, bool isWindows) =>
        isWindows && env("CLIPPY_LIVE") == "1" && env("CLIPPY_LIVE_CLAUDE") == "1";

    public static bool Enabled => IsEnabled(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());

    /// <summary>Pure. Blank/unset gives DefaultModel; a valid value gives it (trimmed); anything else gives null.</summary>
    public static string? ModelFrom(Func<string, string?> env)
    {
        var v = env(ModelVariable);
        if (string.IsNullOrWhiteSpace(v)) return DefaultModel;
        var t = v.Trim();
        return ClaudeCliArgs.IsValidModel(t) ? t : null;
    }

    /// <summary>Skips unless both variables are set and the model name is valid. Returns the model.</summary>
    public static string RequireEnabled()
    {
        if (!Enabled) Assert.Skip(NotEnabled);
        var model = ModelFrom(Environment.GetEnvironmentVariable);
        if (model is null) Assert.Skip(BadModelVariable);
        return model!;
    }

    public static AiDraftingSettings Settings(string model, DateTimeOffset now)
    {
        var s = new AiDraftingSettings
        {
            Enabled = false,
            Backend = AiBackend.ClaudeCode,
            Model = model,
            Effort = Effort,
            TimeoutSeconds = 120,
            ClaudeCliPath = "",
        };
        s.RecordConsent(now);
        return s;
    }

    /// <summary>The production model over the production runner, with no logger. workDir must be under %TEMP%\clippy-live.</summary>
    public static ClaudeCliModel CreateModel(Func<AiDraftingSettings> settings, string workDir)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clippy-live")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(workDir);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The live Claude work directory must be under the live temp root.", nameof(workDir));
        Directory.CreateDirectory(full);
        return new ClaudeCliModel(settings, ClaudeCliLocator.Default, new ClaudeCliRunner(full));
    }

    /// <summary>Skips with NotReady(status.Text) when not ready. A Ready text is never printed.</summary>
    public static async Task RequireReadyAsync(IDraftModel model, CancellationToken ct)
    {
        var status = await model.CheckAsync(ct).ConfigureAwait(false);
        if (!status.Ready) Assert.Skip(NotReady(status.Text));
    }

    /// <summary>The first n.n.n in a status text, or "?".</summary>
    public static string VersionOf(string statusText)
    {
        var m = VersionRx.Match(statusText ?? "");
        return m.Success ? m.Value : "?";
    }
}

/// <summary>
/// The test seam for the manual path: the production OutlookAiMailbox, but the selection is a fixed entry id, so no
/// Outlook window or UI selection is needed. ShowDrafts never touches the user's window.
/// </summary>
internal sealed class FixedSelectionMailbox(IAiMailbox inner, SelectedEmail selection) : IAiMailbox
{
    private int _selectionCalls;

    public int SelectionCalls => Volatile.Read(ref _selectionCalls);

    public Task<CandidateFacts?> GetFactsAsync(string mailbox, string entryId, WorkPriority priority, CancellationToken ct) =>
        inner.GetFactsAsync(mailbox, entryId, priority, ct);

    public Task<AiIdentity> GetIdentityAsync(WorkPriority priority, CancellationToken ct) =>
        inner.GetIdentityAsync(priority, ct);

    public Task<SelectedEmail?> GetSelectedAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _selectionCalls);
        return Task.FromResult<SelectedEmail?>(selection);
    }

    public Task<LiveEmail?> ReadEmailAsync(string entryId, CancellationToken ct) =>
        inner.ReadEmailAsync(entryId, ct);

    public Task<bool> ShowDraftsAsync(CancellationToken ct) => Task.FromResult(false);
}

/// <summary>Wraps the real model. Checks every request before the CLI runs, so a live test can't send more than planned.</summary>
internal sealed class GuardedModel(IDraftModel inner, string expectedModel, string requiredToken) : IDraftModel
{
    /// <summary>Null while no check failed; else a fixed reason (never request text).</summary>
    public string? Tripped { get; private set; }

    /// <summary>RunAsync calls that reached the inner model.</summary>
    public int Calls { get; private set; }

    public ModelResult? LastResult { get; private set; }

    /// <summary>Wall time of the inner call.</summary>
    public TimeSpan? LastDuration { get; private set; }

    public Task<ModelStatus> CheckAsync(CancellationToken ct) => inner.CheckAsync(ct);

    public async Task<ModelResult> RunAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Calls >= 1 ? "second call"
            : !string.Equals(request.Model, expectedModel, StringComparison.Ordinal) ? "model"
            : !string.Equals(request.Effort, LiveClaude.Effort, StringComparison.Ordinal) ? "effort"
            : !request.UserPrompt.Contains(requiredToken, StringComparison.Ordinal) ? "token"
            : !request.UserPrompt.Contains("<thread>\n</thread>", StringComparison.Ordinal) ? "thread"
            : !request.SystemPrompt.Contains(DraftPrompt.NoStyleGuideText, StringComparison.Ordinal) ? "style"
            : request.UserPrompt.Contains("signature_added: yes", StringComparison.Ordinal) ? "signature"
            : null;
        if (reason is not null)
        {
            Tripped = reason;
            return new ModelResult.Failed(ModelFailure.BadInput, null);
        }

        Calls++;
        var watch = Stopwatch.StartNew();
        var result = await inner.RunAsync(request, ct).ConfigureAwait(false);
        watch.Stop();
        LastResult = result;
        LastDuration = watch.Elapsed;
        return result;
    }
}

/// <summary>Wraps the real writer. Refuses anything that isn't the expected AI reply before any COM call.</summary>
internal sealed class RecordingReplyWriter(IReplyDraftWriter inner, string expectedSourceId, Action<RepliedDraft> onCreated)
    : IReplyDraftWriter
{
    private readonly List<ReplyPlan> _plans = [];

    public IReadOnlyList<ReplyPlan> Plans => _plans;

    /// <exception cref="InvalidOperationException">The marker isn't ai, the id isn't the source, or the mailbox isn't null.</exception>
    public async Task<RepliedDraft> ReplyAsync(ReplyPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var refused = !string.Equals(plan.Marker, DraftMarker.Ai, StringComparison.Ordinal) ? "marker"
            : !string.Equals(plan.Id, expectedSourceId, StringComparison.Ordinal) ? "id"
            : plan.Mailbox is not null ? "mailbox"
            : null;
        if (refused is not null)
            throw new InvalidOperationException("Live test refused the reply plan: " + refused + ".");

        var made = await inner.ReplyAsync(plan, ct).ConfigureAwait(false);
        _plans.Add(plan);
        onCreated(made);
        return made;
    }
}

/// <summary>Pure checks of the HTML an AI reply inserts and of the saved draft. Failure messages are fixed texts.</summary>
internal static partial class AiHtmlChecks
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "<p>", "</p>", "<br>", "</br>", "<ul>", "</ul>", "<ol>", "</ol>", "<li>", "</li>", "<b>", "</b>",
        "<strong>", "</strong>", "<i>", "</i>", "<em>", "</em>", "<u>", "</u>", "<blockquote>", "</blockquote>",
        DraftHtml.HighlightOpen, DraftHtml.HighlightClose,
    };

    [GeneratedRegex(@"<a\b[^>]*\bhref\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LinkRx();

    [GeneratedRegex(@"<img\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ImageRx();

    [GeneratedRegex(@"<script\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ScriptRx();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SpacesRx();

    /// <summary>True when the fragment is non-blank and every tag in it is on the allow-list (or the placeholder highlight).</summary>
    public static bool FragmentIsSanitized(string? fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return false;
        var i = 0;
        while (true)
        {
            var open = fragment.IndexOf('<', i);
            if (open < 0) return true;
            var close = fragment.IndexOf('>', open + 1);
            if (close < 0) return false;
            if (!AllowedTags.Contains(fragment.Substring(open, close - open + 1))) return false;
            i = close + 1;
        }
    }

    /// <summary>Counts links with an href, images and scripts in html (case-insensitive). Null gives zeros.</summary>
    public static (int Links, int Images, int Scripts) Active(string? html)
    {
        if (string.IsNullOrEmpty(html)) return (0, 0, 0);
        return (LinkRx().Count(html), ImageRx().Count(html), ScriptRx().Count(html));
    }

    private static string Normalize(string text) =>
        SpacesRx().Replace(text.Replace(' ', ' '), " ").Trim();

    /// <summary>Plain-text containment that ignores layout: the first 20 characters of the fragment's text are in the body.</summary>
    public static bool StartOfFragmentIn(string fragmentHtml, string plainBody)
    {
        var needle = Normalize(DraftHtml.ToPlainText(fragmentHtml));
        if (needle.Length == 0) return false;
        if (needle.Length > 20) needle = needle[..20];
        return Normalize(plainBody ?? "").Contains(needle, StringComparison.Ordinal);
    }

    internal static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
}
