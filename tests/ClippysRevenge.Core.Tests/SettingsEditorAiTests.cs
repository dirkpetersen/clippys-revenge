using ClippysRevenge.Core;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class SettingsEditorAiTests
{
    private static Settings Live() => Settings.CreateDefault(Path.GetTempPath());

    private static readonly DateTimeOffset T1 = DateTimeOffset.Parse("2026-11-13T10:00:00Z");
    private static readonly DateTimeOffset T2 = DateTimeOffset.Parse("2026-11-13T11:00:00Z");

    [Theory]
    [InlineData("AiDrafting.Enabled")]
    [InlineData("AiDrafting.ExcludeSenders[3]")]
    [InlineData("AiDrafting.ClaudeCliPath")]
    [InlineData("AiDrafting.TimeoutSeconds")]
    public void TabFor_maps_ai_fields(string field) => Assert.Equal(SettingsTab.AiDrafts, SettingsEditor.TabFor(field));

    [Fact]
    public void AiDrafts_is_the_last_tab_and_index_four() => Assert.Equal(4, (int)SettingsTab.AiDrafts);

    [Fact]
    public void TrySetDouble_parses_and_records_errors()
    {
        var e = new SettingsEditor(Live());
        const string f = "AiDrafting.MinConfidence";
        double v = 0.6;
        Assert.True(e.TrySetDouble(f, "0.75", x => v = x));
        Assert.Equal(0.75, v);
        Assert.True(e.TrySetDouble(f, " 0,5 ", x => v = x));
        Assert.Equal(0.5, v);
        Assert.True(e.TrySetDouble(f, "1e-1", x => v = x));
        Assert.Equal(0.1, v);
        foreach (var bad in new[] { "abc", "NaN", "Infinity", "1,000.5", "" })
        {
            v = 0.3;
            Assert.False(e.TrySetDouble(f, bad, x => v = x));
            Assert.Equal(0.3, v);
            Assert.Contains(e.Errors(), er => er.Field == f && er.Message == SettingsEditor.NumberMessage);
        }
        Assert.True(e.TrySetDouble(f, "0.8", x => v = x));
        Assert.DoesNotContain(e.Errors(), er => er.Field == f);
    }

    [Fact]
    public void AdoptConsent_only_sets_when_missing()
    {
        var e = new SettingsEditor(Live());
        e.AdoptConsent(T1);
        Assert.Equal(T1, e.Draft.AiDrafting.ConsentedAtUtc);
        e.AdoptConsent(T2);
        Assert.Equal(T1, e.Draft.AiDrafting.ConsentedAtUtc);
    }

    [Fact]
    public void AiMailboxChoices_lists_enabled_shared_mailboxes_once()
    {
        var s = Live();
        s.AddOrEnableSharedMailbox("a@x.org", null).AiDrafts = true;
        s.AddOrEnableSharedMailbox("b@x.org", null).Enabled = false;
        s.SharedMailboxes.Add(new SharedMailbox { Smtp = "A@X.org", Enabled = true });
        var choices = new SettingsEditor(s).AiMailboxChoices();
        var c = Assert.Single(choices);
        Assert.Equal("a@x.org", c.Key);
        Assert.Equal(s.SharedMailboxes[0].MenuLabel, c.Label);
        Assert.True(c.AiDrafts);
    }

    [Fact]
    public void SetAiDrafts_only_for_enabled_mailboxes()
    {
        var s = Live();
        s.AddOrEnableSharedMailbox("a@x.org", null).AiDrafts = true;
        var b = s.AddOrEnableSharedMailbox("b@x.org", null);
        b.AiDrafts = true;
        b.Enabled = false;
        var e = new SettingsEditor(s);
        Assert.True(e.SetAiDrafts("a@x.org", false));
        Assert.False(e.Draft.FindSharedMailbox("a@x.org")!.AiDrafts);
        Assert.False(e.SetAiDrafts("b@x.org", false));
        Assert.True(e.Draft.FindSharedMailbox("b@x.org")!.AiDrafts);
        Assert.False(e.SetAiDrafts("zzz@x.org", true));
    }

    [Fact]
    public void Rebase_adopts_consent_given_in_the_tray()
    {
        var live = Live();
        var e = new SettingsEditor(live);
        live.AiDrafting.RecordConsent(T1);
        live.AiDrafting.SetEnabled(true, T1);
        e.Draft.AiDrafting.Enabled = true;   // the window ticks Enabled on its own (consent unknown to it)
        e.Rebase(live);
        Assert.Equal(T1, e.Draft.AiDrafting.ConsentedAtUtc);
        Assert.DoesNotContain(e.Errors(), er => er.Field.StartsWith("AiDrafting", StringComparison.Ordinal));
    }

    [Fact]
    public void Rebase_live_consent_always_wins()
    {
        var live = Live();
        live.AiDrafting.RecordConsent(T2);
        var e = new SettingsEditor(live);
        e.Draft.AiDrafting.ConsentedAtUtc = T1;
        e.Rebase(live);
        Assert.Equal(T2, e.Draft.AiDrafting.ConsentedAtUtc);
    }

    [Fact]
    public void Rebase_adopts_a_tray_enable_the_window_did_not_touch()
    {
        var live = Live();
        live.AiDrafting.RecordConsent(T1);
        var e = new SettingsEditor(live);
        live.AiDrafting.SetEnabled(true, T2);
        e.Rebase(live);
        Assert.True(e.Draft.AiDrafting.Enabled);
        Assert.Equal(T2, e.Draft.AiDrafting.EnabledAtUtc);
    }

    [Fact]
    public void Rebase_keeps_the_windows_enabled_when_it_changed_it()
    {
        var live = Live();
        live.AiDrafting.RecordConsent(T1);
        live.AiDrafting.SetEnabled(true, T1);
        var e = new SettingsEditor(live);
        e.Draft.AiDrafting.Enabled = false;
        live.AiDrafting.SetEnabled(false, T2);
        live.AiDrafting.SetEnabled(true, T2);   // tray off then on: Enabled true again
        e.Rebase(live);
        Assert.False(e.Draft.AiDrafting.Enabled);
    }

    [Fact]
    public void Rebase_adopts_enabled_at_after_a_tray_off_on()
    {
        var live = Live();
        live.AiDrafting.RecordConsent(T1);
        live.AiDrafting.SetEnabled(true, T1);
        var e = new SettingsEditor(live);
        live.AiDrafting.SetEnabled(false, T1.AddMinutes(1));
        live.AiDrafting.SetEnabled(true, T2);
        e.Rebase(live);
        Assert.True(e.Draft.AiDrafting.Enabled);
        Assert.Equal(T2, e.Draft.AiDrafting.EnabledAtUtc);
    }

    [Fact]
    public void Rebase_does_not_merge_other_ai_fields()
    {
        var live = Live();
        var e = new SettingsEditor(live);
        live.AiDrafting.DailyCap = 50;
        e.Rebase(live);
        Assert.Equal(AiDraftingSettings.DefaultDailyCap, e.Draft.AiDrafting.DailyCap);
    }
}