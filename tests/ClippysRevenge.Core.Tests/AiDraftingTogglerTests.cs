using ClippysRevenge.Core;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public sealed class AiDraftingTogglerTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T20:00:00Z");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-tog-" + Guid.NewGuid().ToString("N"));
    private readonly Settings _settings;
    private readonly List<Settings> _saves = [];
    private readonly ManualTimeProvider _time = new();
    private int _changed;
    private Exception? _saveThrows;
    private readonly AiDraftingToggler _t;

    public AiDraftingTogglerTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = Settings.CreateDefault(_dir);
        _time.Advance(Now - ManualTimeProvider.T0);
        _t = new AiDraftingToggler(_settings, s =>
        {
            if (_saveThrows is not null) throw _saveThrows;
            _saves.Add(s);
        }, () => _changed++, _time);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void RecordConsent_records_saves_and_notifies()
    {
        var c = _t.RecordConsent();
        Assert.Equal(Now, c);
        Assert.Equal(Now, _settings.AiDrafting.ConsentedAtUtc);
        Assert.Equal(Now, Assert.Single(_saves).AiDrafting.ConsentedAtUtc);
        Assert.Equal(1, _changed);
    }

    [Fact]
    public void RecordConsent_with_existing_consent_does_nothing()
    {
        var old = Now.AddDays(-3);
        _settings.AiDrafting.RecordConsent(old);
        Assert.Equal(old, _t.RecordConsent());
        Assert.Empty(_saves);
        Assert.Equal(0, _changed);
    }

    [Fact]
    public void SetEnabled_true_with_consent()
    {
        _settings.AiDrafting.RecordConsent(Now.AddDays(-1));
        _t.SetEnabled(true);
        Assert.True(_settings.AiDrafting.Enabled);
        Assert.Equal(Now, _settings.AiDrafting.EnabledAtUtc);
        Assert.Single(_saves);
        Assert.Equal(1, _changed);
    }

    [Fact]
    public void SetEnabled_true_without_consent_throws_and_changes_nothing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _t.SetEnabled(true));
        Assert.Equal(AiDraftingSettings.ConsentRequiredMessage, ex.Message);
        Assert.Empty(_saves);
        Assert.Equal(0, _changed);
        Assert.False(_settings.AiDrafting.Enabled);
    }

    [Fact]
    public void SetEnabled_to_the_same_value_is_a_no_op()
    {
        _settings.AiDrafting.RecordConsent(Now.AddDays(-1));
        var before = Now.AddHours(-5);
        _settings.AiDrafting.SetEnabled(true, before);
        _t.SetEnabled(true);
        Assert.Equal(before, _settings.AiDrafting.EnabledAtUtc);
        Assert.Empty(_saves);
        Assert.Equal(0, _changed);
    }

    [Fact]
    public void SetEnabled_false_keeps_enabled_at()
    {
        _settings.AiDrafting.RecordConsent(Now.AddDays(-1));
        var at = Now.AddHours(-5);
        _settings.AiDrafting.SetEnabled(true, at);
        _t.SetEnabled(false);
        Assert.False(_settings.AiDrafting.Enabled);
        Assert.Equal(at, _settings.AiDrafting.EnabledAtUtc);
        Assert.Single(_saves);
    }

    [Fact]
    public void A_failing_save_leaves_live_alone_and_frees_the_gate()
    {
        _settings.AiDrafting.RecordConsent(Now.AddDays(-1));
        var before = _settings.AiDrafting;
        _saveThrows = new IOException("disk");
        Assert.Throws<IOException>(() => _t.SetEnabled(true));
        Assert.Same(before, _settings.AiDrafting);
        Assert.False(before.Enabled);
        Assert.Equal(0, _changed);
        _saveThrows = null;
        _t.SetEnabled(true);
        Assert.True(_settings.AiDrafting.Enabled);
    }

    [Fact]
    public void Changes_are_copy_on_write()
    {
        _settings.AiDrafting.RecordConsent(Now.AddDays(-1));
        var before = _settings.AiDrafting;
        _t.SetEnabled(true);
        Assert.NotSame(before, _settings.AiDrafting);
        Assert.False(before.Enabled);
    }

    [Fact]
    public async Task The_gate_is_honoured()
    {
        var ct = TestContext.Current.CancellationToken;
        _settings.AiDrafting.RecordConsent(Now.AddDays(-1));
        await _settings.Gate.WaitAsync(ct);
        var task = Task.Run(() => _t.SetEnabled(true), ct);
        Assert.NotSame(task, await Task.WhenAny(task, Task.Delay(200, ct)));
        _settings.Gate.Release();
        await task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(_settings.AiDrafting.Enabled);
    }

    [Fact]
    public void Null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new AiDraftingToggler(null!, _ => { }, () => { }));
        Assert.Throws<ArgumentNullException>(() => new AiDraftingToggler(_settings, null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => new AiDraftingToggler(_settings, _ => { }, null!));
    }
}