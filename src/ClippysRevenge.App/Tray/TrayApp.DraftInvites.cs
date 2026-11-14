using System.Runtime.InteropServices;
using ClippysRevenge.Core;

namespace ClippysRevenge.App.Tray;

/// <summary>The Draft invites submenu (SPEC 6.2, 22.3). UI thread only unless noted.</summary>
internal sealed partial class TrayApp
{
    private const int AsfwAny = -1;

    private readonly ToolStripMenuItem _invites = new("Draft invites") { Visible = false };
    private int _invitesQueued;      // coalesced Changed -> UI post (Interlocked)
    private int _invitesChecking;    // one menu-open check in flight (Interlocked)
    private readonly HashSet<string> _invitesOpening = new(StringComparer.OrdinalIgnoreCase); // UI thread only

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    private void AttachDraftInvites()
    {
        if (_s.DraftInvites is not { } d)
        {
            return;
        }
        d.Invites.Changed += OnDraftInvitesChanged;
        FillDraftInvites();
    }

    private void DetachDraftInvites()
    {
        if (_s.DraftInvites is { } d)
        {
            d.Invites.Changed -= OnDraftInvitesChanged;
        }
    }

    // Any thread: only queue one coalesced refill on the UI thread.
    private void OnDraftInvitesChanged()
    {
        if (Interlocked.Exchange(ref _invitesQueued, 1) == 0)
        {
            PostToUi(() =>
            {
                Interlocked.Exchange(ref _invitesQueued, 0);
                FillDraftInvites();
            });
        }
    }

    private void FillDraftInvites()
    {
        if (_s.DraftInvites is not { } d)
        {
            _invites.Visible = false;
            return;
        }
        var model = DraftInviteMenu.Build(d.Invites.Pending, d.State(), _s.GetSettings().SharedMailboxes, d.TimeZone);
        _invites.Visible = model.Visible;
        _invites.Text = DraftInviteMenu.MenuText(model.Header);

        var old = _invites.DropDownItems.Cast<ToolStripItem>().ToList();
        _invites.DropDownItems.Clear();
        foreach (var item in old)
        {
            item.Dispose();
        }
        foreach (var line in model.Lines)
        {
            var entry = line.Entry;
            var item = new ToolStripMenuItem(line.Text)
            {
                Enabled = entry is not null && model.LinesEnabled && !_invitesOpening.Contains(entry.EntryId),
            };
            if (entry is not null)
            {
                item.Click += (_, _) => OpenDraftInvite(entry);
            }
            _invites.DropDownItems.Add(item);
        }
        if (model.StartOutlookHint)
        {
            _invites.DropDownItems.Add(new ToolStripMenuItem(DraftInviteMenu.StartOutlookHintText) { Enabled = false });
        }
    }

    private void OnMenuOpeningDraftInvites()
    {
        FillDraftInvites();
        if (_s.DraftInvites is { } d && d.State() == OutlookConnectionState.Available
            && Interlocked.Exchange(ref _invitesChecking, 1) == 0)
        {
            _ = CheckDraftInvitesAsync(d);
        }
    }

    private async Task CheckDraftInvitesAsync(DraftInviteTrayServices d)
    {
        try
        {
            using var cts = new CancellationTokenSource(ListTimeout);
            await d.Invites.CheckAsync(WorkPriority.Interactive, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Slow or gone Outlook: the list stays as it is. A change raises Changed, which refills the menu.
        }
        finally
        {
            Interlocked.Exchange(ref _invitesChecking, 0);
        }
    }

    private async void OpenDraftInvite(DraftInviteEntry entry)
    {
        if (_s.DraftInvites is not { } d || !_invitesOpening.Add(entry.EntryId))
        {
            return;
        }
        try
        {
            // The tray owns the foreground right after a menu click; let Outlook's Activate() take it.
            AllowSetForegroundWindow(AsfwAny);
            var text = await DraftInviteClick.OpenAsync(d.Window, entry, d.Invites.Forget, null, CancellationToken.None);
            if (!_disposed && text is not null)
            {
                _s.Balloons.PostUserFeedback(text);
            }
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                _s.Balloons.PostUserFeedback(DraftInviteMenu.OpenFailedText);
            }
        }
        finally
        {
            _invitesOpening.Remove(entry.EntryId);
            if (!_disposed)
            {
                FillDraftInvites();
            }
        }
    }
}
