using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

public sealed record SettingsApplyTargets(
    Settings Live,
    SettingsStore Store,
    TokenHolder Tokens,
    Func<int, Task<bool>> RestartServer,
    Func<int?> ServerPort,
    Action<bool> SetStartWithWindows,
    MailboxIndexes? Indexes,
    Action? RetentionChanged,
    Action? SyncNow,
    Action? AiSettingsChanged = null);

/// <summary>Errors: nothing was applied. Warnings: applied, but a side effect failed.</summary>
public sealed record SettingsApplyResult(IReadOnlyList<SettingsError> Errors, IReadOnlyList<string> Warnings)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>Applies the Settings window's staged state to the live settings (SPEC 6.3, 5.2).</summary>
public sealed class SettingsApplier
{
    private readonly SettingsApplyTargets _t;
    private readonly ILogger? _logger;
    private SemaphoreSlim _gate => _t.Live.Gate;

    /// <exception cref="ArgumentNullException">targets or any non-nullable member is null.</exception>
    public SettingsApplier(SettingsApplyTargets targets, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(targets.Live);
        ArgumentNullException.ThrowIfNull(targets.Store);
        ArgumentNullException.ThrowIfNull(targets.Tokens);
        ArgumentNullException.ThrowIfNull(targets.RestartServer);
        ArgumentNullException.ThrowIfNull(targets.ServerPort);
        ArgumentNullException.ThrowIfNull(targets.SetStartWithWindows);
        _t = targets;
        _logger = logger;
    }

    /// <summary>Never throws except OperationCanceledException for ct. Serialized.</summary>
    public async Task<SettingsApplyResult> ApplyAsync(SettingsEditor editor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(editor);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ApplyCoreAsync(editor).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SettingsApplyResult> ApplyCoreAsync(SettingsEditor editor)
    {
        var live = _t.Live;
        editor.Rebase(live); // tray toggles since the window opened must not be reverted or undone
        var errors = editor.Errors();
        if (errors.Count > 0) return new SettingsApplyResult(errors, []);
        var changes = editor.Changes(live);
        if (!changes.Any) return new SettingsApplyResult([], []);

        var draft = editor.Draft;
        var warnings = new List<string>();
        var oldPort = live.Port;
        bool portMoved = false;

        if (changes.PortChanged)
        {
            var wasRunning = _t.ServerPort() is not null;
            var ok = await TryRestartAsync(draft.Port).ConfigureAwait(false);
            if (!ok)
            {
                if (wasRunning) await TryRestartAsync(oldPort).ConfigureAwait(false);
                return Fail(new SettingsError("Port",
                    $"Port {draft.Port} is in use or can't be opened. The server stays on port {oldPort}."));
            }
            portMoved = true;
        }

        try
        {
            _t.Store.Save(draft);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (portMoved) await TryRestartAsync(oldPort).ConfigureAwait(false);
            return Fail(new SettingsError("", "Couldn't save settings: " + ex.Message));
        }

        if (editor.PendingToken is { } token)
        {
            try
            {
                _t.Store.SaveToken(token);
                _t.Tokens.Set(token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add("Couldn't save the new auth token; the old one still works.");
            }
        }

        var changedFields = ChangedFields(live, draft, changes);
        live.CopyFrom(draft);

        if (_t.Indexes is { } indexes)
        {
            foreach (var key in changes.EnabledMailboxes)
            {
                try { indexes.Add(key); }
                catch (Exception ex) { warnings.Add($"Couldn't open the index for {key}: {ex.Message}"); }
            }
            foreach (var key in changes.DisabledMailboxes)
            {
                try { indexes.Remove(key, changes.DeleteIndexes.Contains(key)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Couldn't delete the index file for {key}; it was kept.");
                }
                catch (Exception ex) when (ex is ArgumentException or ObjectDisposedException)
                {
                    warnings.Add($"Couldn't close the index for {key}: {ex.Message}");
                }
            }
        }

        if (changes.StartWithWindowsChanged)
        {
            try { _t.SetStartWithWindows(live.StartWithWindows); }
            catch (Exception ex) { warnings.Add("Couldn't update the Windows startup entry: " + ex.Message); }
        }

        if (changes.RetentionChanged) _t.RetentionChanged?.Invoke();
        else if (changes.SyncPlanChanged) _t.SyncNow?.Invoke();

        if (_t.AiSettingsChanged is { } notify)
        {
            try { notify(); }
            catch (Exception ex)
            {
                _logger?.LogWarning("Couldn't notify AI drafting of the change ({Type}).", ex.GetType().Name);
            }
        }

        _logger?.LogInformation("Settings applied: {Fields}", string.Join(", ", changedFields));
        foreach (var w in warnings) _logger?.LogWarning("Settings warning: {Warning}", w);
        return new SettingsApplyResult([], warnings);
    }

    private static SettingsApplyResult Fail(SettingsError e) => new([e], []);

    private async Task<bool> TryRestartAsync(int port)
    {
        try
        {
            return await _t.RestartServer(port).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Restarting the server on port {Port} failed ({Type}).", port, ex.GetType().Name);
            return false;
        }
    }

    private static readonly PropertyInfo[] Props = typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite).ToArray();

    private static List<string> ChangedFields(Settings live, Settings draft, SettingsChanges changes)
    {
        var names = new List<string>();
        foreach (var p in Props)
            if (JsonSerializer.Serialize(p.GetValue(live)) != JsonSerializer.Serialize(p.GetValue(draft)))
                names.Add(p.Name);
        if (changes.TokenChanged) names.Add("AuthToken");
        return names;
    }
}
