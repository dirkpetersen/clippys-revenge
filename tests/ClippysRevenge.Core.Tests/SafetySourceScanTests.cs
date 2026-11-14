using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace ClippysRevenge.Core.Tests;

/// <summary>
/// Source scan of the hard rules (SPEC section 1, CLAUDE.md): no Send, no Outlook launch via COM,
/// loopback only, no Graph, replies only via ReplySubject. Comments are always stripped; string
/// literals are blanked for code checks and kept for literal checks.
/// </summary>
public class SafetySourceScanTests
{
    private sealed record Src(string Rel, string Code, string WithStrings, string[] Literals);

    private static readonly Lazy<(string Root, List<Src> Files)> Scan = new(Load);

    private static (string, List<Src>) Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !(Directory.Exists(Path.Combine(dir.FullName, "src")) && File.Exists(Path.Combine(dir.FullName, "SPEC.md"))))
        {
            dir = dir.Parent;
        }
        Assert.True(dir is not null, "Repo root (with src/ and SPEC.md) not found above " + AppContext.BaseDirectory);
        var src = Path.Combine(dir!.FullName, "src");
        var files = new List<Src>();
        foreach (var f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(dir.FullName, f).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/"))
            {
                continue;
            }
            var text = File.ReadAllText(f);
            var lits = new List<string>();
            var code = Strip(text, blankStrings: true, lits: null);
            var withStr = Strip(text, blankStrings: false, lits);
            files.Add(new Src(rel, code, withStr, [.. lits]));
        }
        Assert.NotEmpty(files);
        return (dir.FullName, files);
    }

    /// <summary>
    /// Removes comments (newlines kept so line numbers hold). String and char literals are blanked to
    /// empty quotes when blankStrings is true; otherwise kept, and their text appended to lits as "line|text".
    /// </summary>
    internal static string Strip(string s, bool blankStrings, List<string>? lits)
    {
        var o = new StringBuilder(s.Length);
        var line = 1;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var n = i + 1 < s.Length ? s[i + 1] : '\0';
            if (c == '/' && n == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
            }
            else if (c == '/' && n == '*')
            {
                i += 2;
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'))
                {
                    if (s[i] == '\n') { o.Append('\n'); line++; }
                    i++;
                }
                i += 2;
            }
            else if (c == '"' || ((c == '@' || c == '$') && IsStringStart(s, i)))
            {
                var start = i;
                var verbatim = false;
                while (s[i] != '"') { if (s[i] == '@') verbatim = true; i++; }
                var quotes = 0;
                while (i < s.Length && s[i] == '"') { quotes++; i++; }
                var startLine = line;
                var body = new StringBuilder();
                if (quotes >= 3)
                {
                    var close = new string('"', quotes);
                    var end = s.IndexOf(close, i, StringComparison.Ordinal);
                    if (end < 0) end = s.Length;
                    body.Append(s, i, end - i);
                    i = Math.Min(s.Length, end + quotes);
                }
                else if (quotes == 1)
                {
                    while (i < s.Length)
                    {
                        if (verbatim && s[i] == '"' && i + 1 < s.Length && s[i + 1] == '"') { body.Append('"'); i += 2; continue; }
                        if (!verbatim && s[i] == '\\' && i + 1 < s.Length) { body.Append(s[i + 1]); i += 2; continue; }
                        if (s[i] == '"') { i++; break; }
                        if (s[i] == '\n' && !verbatim) break;
                        body.Append(s[i]);
                        i++;
                    }
                }
                // quotes == 2: empty string.
                var nl = s.AsSpan(start, i - start).Count('\n');
                if (blankStrings)
                {
                    o.Append("\"\"");
                    o.Append('\n', nl);
                }
                else
                {
                    o.Append(s, start, i - start);
                    lits?.Add($"{startLine}|{body}");
                }
                line += nl;
            }
            else if (c == '\'')
            {
                // char literal: 'x', '\x' or '\u0000'
                var j = i + 1;
                if (j < s.Length && s[j] == '\\') j++;
                j++;
                while (j < s.Length && s[j] != '\'' && s[j] != '\n') j++;
                var endExcl = Math.Min(j + 1, s.Length);
                o.Append(blankStrings ? "''" : s.Substring(i, endExcl - i));
                i = endExcl;
            }
            else
            {
                if (c == '\n') line++;
                o.Append(c);
                i++;
            }
        }
        return o.ToString();
    }

    private static bool IsStringStart(string s, int i)
    {
        while (i < s.Length && (s[i] == '@' || s[i] == '$')) i++;
        return i < s.Length && s[i] == '"';
    }

    private static int LineOf(string text, int index) => 1 + text.AsSpan(0, index).Count('\n');

    private static List<string> Find(Func<Src, string> pick, string pattern, Func<Src, bool>? skip = null, RegexOptions opts = RegexOptions.None)
    {
        var re = new Regex(pattern, opts);
        var hits = new List<string>();
        foreach (var f in Scan.Value.Files)
        {
            if (skip?.Invoke(f) == true) continue;
            var t = pick(f);
            foreach (Match m in re.Matches(t))
            {
                hits.Add($"{f.Rel}:{LineOf(t, m.Index)}: {m.Value.Trim()}");
            }
        }
        return hits;
    }

    private static void AssertNone(List<string> hits, string rule) =>
        Assert.True(hits.Count == 0, rule + Environment.NewLine + string.Join(Environment.NewLine, hits));

    private const string TrayFile = "src/ClippysRevenge.App/Tray/TrayApp.cs";
    private const string Launcher = "src/ClippysRevenge.App/OutlookLauncher.cs";
    private const string ClaudeCliFile = "src/ClippysRevenge.Core/ClaudeCli.cs";

    // ---- Send ----

    private const string SendRegex = @"\.\s*Send\s*\(";

    [Fact]
    public void NoSend_Anywhere() =>
        AssertNone(Find(f => f.Code, SendRegex),
            "Clippy never sends anything: no .Send( in src/ (CLAUDE.md hard rule, SPEC §1.4, §22).");

    [Fact]
    public void NoSendByLateBinding()
    {
        var lits = Scan.Value.Files.SelectMany(f => f.Literals.Select(l => (f.Rel, Text: l[(l.IndexOf('|') + 1)..])))
            .Where(x => string.Equals(x.Text.Trim(), "Send", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.Rel}: \"{x.Text}\"").ToList();
        AssertNone(lits, "No string literal may equal \"Send\" (late-bound send).");
        AssertNone(Find(f => f.Code, @"\bInvokeMember\b"), "InvokeMember is forbidden (late-bound send).");
    }

    [Fact]
    public void SendMatchers_CatchPlantedViolations()
    {
        Assert.Matches(SendRegex, "item.Send();");
        Assert.Matches(SendRegex, "item . Send ()");
        Assert.Matches(SendRegex, "((Outlook.AppointmentItem)o).Send();");
        Assert.DoesNotMatch(SendRegex, "client.SendAsync(req)");
        Assert.DoesNotMatch(SendRegex, "x.Sender(");
        Assert.DoesNotMatch(SendRegex, "Send(x)");
        Assert.DoesNotMatch(SendRegex, Strip("// item.Send()\nvar s = \"item.Send()\";", blankStrings: true, lits: null));

        var lits = new List<string>();
        Strip("var v = \"Send\";", blankStrings: false, lits);
        Assert.Contains(lits, l => l[(l.IndexOf('|') + 1)..] == "Send");
        Assert.Matches(@"\bInvokeMember\b", "t.InvokeMember(\"x\")");
    }

    [Fact]
    public void RemovedInviteTypes_Absent()
    {
        AssertNone(Find(f => f.Code,
            @"\b(InviteGate|InviteDecision|InviteAction|InviteChoice|InviteConfirmForm|InviteConfirmQueue|InviteConfirmPresenter|IInviteConfirmer|AwaitingConfirmation|InviteConfirmTimeoutSeconds)\b"),
            "The send/confirm types were removed in M15.1.");
        var files = Scan.Value.Files.Select(f => Path.GetFileName(f.Rel)).ToList();
        foreach (var gone in new[] { "InviteGate.cs", "InviteConfirm.cs", "InviteConfirmForm.cs", "InviteConfirmPresenter.cs" })
            Assert.DoesNotContain(gone, files);
        AssertNone(Find(f => f.WithStrings, @"""[^""\n]*send_meeting_invite[^""\n]*"""), "send_meeting_invite no longer exists.");
    }

    [Fact]
    public void CreateMeetingDraftTool_ReplyToId()
    {
        var f = Scan.Value.Files.Single(x => x.Rel == "src/ClippysRevenge.App/McpTools.cs");
        Assert.Contains("string? reply_to_id = null", f.Code);
        Assert.Contains("reply_to_id, cancellationToken)", f.Code);
    }

    [Fact]
    public void MeetingNote_uses_the_reply_path()
    {
        var f = Scan.Value.Files.Single(x => x.Rel == "src/ClippysRevenge.Core/MeetingNote.cs");
        Assert.Contains("IReplyDraftWriter", f.Code);
        Assert.Contains("DraftMarker.Tool", f.Code);
        Assert.DoesNotMatch(@"\bOutlook\.|\bMarshal\b|Subject\s*=", f.Code);
    }

    [Fact]
    public void CreateMeetingDraftTool_Annotations()
    {
        var f = Scan.Value.Files.Single(x => x.Rel == "src/ClippysRevenge.App/McpTools.cs");
        Assert.Contains("Name = \"create_meeting_draft\", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false", f.WithStrings);
        Assert.Contains("[Description(InviteTexts.ToolDescription)]", f.WithStrings);
        Assert.DoesNotMatch(@"\bbool\s+teams\b", f.Code);
    }

    [Fact]
    public void CreateMeeting_SavesOnly()
    {
        var f = Scan.Value.Files.Single(x => x.Rel.EndsWith("OutlookCalendar.Invites.cs", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(f.Code, @"\.\s*Save\s*\(\s*\)"));
        var idx = f.Code.IndexOf("CreateMeeting(", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        Assert.DoesNotMatch(@"\bdecision\b", f.Code[idx..]);
    }
    // ---- Outlook launch ----

    [Fact]
    public void NoComLaunchCalls_Anywhere()
    {
        AssertNone(Find(f => f.Code,
            @"new\s+(Outlook\.)?Application\s*\(|\bActivator\s*\.\s*CreateInstance|CoCreateInstance|GetTypeFromProgID|GetTypeFromCLSID|\bCreateObject\s*\(|new\s+Outlook\.ApplicationClass"),
            "COM launch / instantiation calls are forbidden (Outlook is attached via GetActiveObject only).");
    }

    [Fact]
    public void ProcessStart_OnlyInOutlookLauncher_AndLauncherUsesNoCom()
    {
        AssertNone(Find(f => f.Code, @"\bProcess\s*\.\s*Start\b|\bShellExecute\w*\s*\(", f => f.Rel == Launcher || f.Rel == TrayFile || f.Rel == ClaudeCliFile || f.Rel == RestarterFile),
            "Only OutlookLauncher.cs, TrayApp.cs (ShellOpen: folders, the skill file, the manual link), ClaudeCli.cs and UpdateRestarter.cs (the update handover) may start a process.");
        // TrayApp.ShellOpen hands one path or URL to the shell (not Outlook); it is the only other Process.Start.
        var tray = Scan.Value.Files.Single(f => f.Rel == TrayFile);
        Assert.Single(Regex.Matches(tray.Code, @"\bProcess\s*\.\s*Start\b"));
        Assert.Matches(@"void\s+ShellOpen\s*\(string target\)\s*\{\s*Process\.Start\(new ProcessStartInfo\(target\)\s*\{\s*UseShellExecute\s*=\s*true\s*\}\)", tray.Code);
        Assert.DoesNotMatch(@"\bArgumentList\b|\.Arguments\b|\bVerb\s*=", tray.Code);

        var l = Scan.Value.Files.SingleOrDefault(f => f.Rel == Launcher);
        Assert.NotNull(l);
        Assert.Matches(@"Process\s*\.\s*Start\s*\(", l!.Code);
        Assert.DoesNotMatch(@"Marshal\b|Outlook\.|GetActiveObject|Activator|ProgID", l.Code);

        // UpdateRestarter starts the installed exe only: no arguments, no shell, no verb, no Outlook.
        var r = Scan.Value.Files.SingleOrDefault(f => f.Rel == RestarterFile);
        Assert.NotNull(r);
        Assert.Single(Regex.Matches(r!.Code, @"\bProcess\s*\.\s*Start\b"));
        Assert.Matches(@"UseShellExecute\s*=\s*false", r.Code);
        Assert.DoesNotMatch(@"\bArgumentList\b|\.Arguments\b|\bVerb\s*=|UseShellExecute\s*=\s*true", r.Code);
        Assert.DoesNotMatch(@"\bOutlook\b|GetActiveObject|Marshal\b|ProgID|Activator", r.Code);
        Assert.Matches(@"new\s+ProcessStartInfo\s*\(\s*paths\s*\.\s*ExePath\s*\)", r.Code);
        Assert.Matches(@"Path\s*\.\s*GetFullPath\s*\(\s*paths\s*\.\s*ExePath\s*\)", r.Code);
    }

    private const string RestarterFile = "src/ClippysRevenge.App/UpdateRestarter.cs";
    private const string UpdateInstallFile = "src/ClippysRevenge.Core/UpdateInstall.cs";
    private const string KillRegex = @"\.\s*Kill\s*\(";
    private const string UpdateHostRegex = @"api\.github\.com|githubusercontent\.com|/releases/download/";
    private const string HttpClientRegex = @"new\s+HttpClient\s*\(";

    [Fact]
    public void Kill_OnlyInKnownProcessOwners()
    {
        // ClaudeCli kills its own headless claude process; UpdateInstall kills the new Clippy process through
        // IUpdateProcess; UpdateRestarter wraps that process. Nothing else may kill a process.
        AssertNone(Find(f => f.Code, KillRegex, f => f.Rel == ClaudeCliFile || f.Rel == UpdateInstallFile || f.Rel == RestarterFile),
            "Process.Kill is allowed only in ClaudeCli.cs, UpdateInstall.cs and UpdateRestarter.cs.");
        foreach (var rel in new[] { ClaudeCliFile, UpdateInstallFile, RestarterFile })
        {
            var f = Scan.Value.Files.Single(x => x.Rel == rel);
            Assert.Single(Regex.Matches(f.Code, KillRegex));
        }
        Assert.DoesNotMatch(@"entireProcessTree", Scan.Value.Files.Single(x => x.Rel == RestarterFile).Code);
    }

    [Fact]
    public void UpdateHosts_OnlyInUpdateRelease()
    {
        const string release = "src/ClippysRevenge.Core/UpdateRelease.cs";
        AssertNone(Find(f => f.WithStrings, UpdateHostRegex, f => f.Rel == release, RegexOptions.IgnoreCase),
            "GitHub release hosts may appear only in UpdateRelease.cs.");
        var f = Scan.Value.Files.Single(x => x.Rel == release);
        Assert.Matches(new Regex(UpdateHostRegex, RegexOptions.IgnoreCase), f.WithStrings);
    }

    [Fact]
    public void NewHttpClient_OnlyInKnownFiles()
    {
        string[] allowed =
        [
            "src/ClippysRevenge.Core/ConnectionTester.cs",
            "src/ClippysRevenge.Core/AnthropicApiModel.cs",
            "src/ClippysRevenge.Core/UpdateClient.cs",
        ];
        AssertNone(Find(f => f.Code, HttpClientRegex, f => allowed.Contains(f.Rel)),
            "new HttpClient is allowed only in ConnectionTester.cs, AnthropicApiModel.cs and UpdateClient.cs.");
    }

    [Fact]
    public void UpdateFiles_NeverTouchOutlookOrMail()
    {
        var files = Scan.Value.Files.Where(f => Path.GetFileName(f.Rel).StartsWith("Update", StringComparison.Ordinal)).ToList();
        var names = files.Select(f => Path.GetFileName(f.Rel)).ToHashSet();
        Assert.Contains("UpdateClient.cs", names);
        Assert.Contains("UpdateInstall.cs", names);
        Assert.Contains("UpdateRestarter.cs", names);
        var re = new Regex(@"\bOutlook\.|\bMarshal\b|GetActiveObject|\bMailItem\b|CreateReplyDraft|\.\s*Send\s*\(|\.\s*Display\s*\(");
        var hits = files.SelectMany(f => re.Matches(f.Code).Select(m => $"{f.Rel}:{LineOf(f.Code, m.Index)}: {m.Value}")).ToList();
        AssertNone(hits, "Update files must not touch Outlook or mail.");
    }

    [Fact]
    public void UpdateMatchers_CatchPlantedViolations()
    {
        Assert.Matches(KillRegex, "p.Kill();");
        Assert.Matches(new Regex(UpdateHostRegex, RegexOptions.IgnoreCase), "\"https://api.github.com/x\"");
        Assert.Matches(HttpClientRegex, "new HttpClient(h)");
    }

    [Fact]
    public void No_unowned_icon_handles() =>
        AssertNone(Find(f => f.Code, @"\bGetHicon\s*\(|\bFromHandle\s*\("),
            "Badged icons come from an in-memory .ico: no GetHicon / Icon.FromHandle (SPEC 22.6).");

    [Fact]
    public void Badge_renderer_has_no_pinvoke()
    {
        var f = Scan.Value.Files.Single(x => x.Rel == "src/ClippysRevenge.App/Tray/TrayBadgeRenderer.cs");
        Assert.DoesNotMatch(@"\bDllImport\b|\bLibraryImport\b", f.Code);
    }

    [Fact]
    public void AiUi_NeverSignsInAndNeverShowsTheKey()
    {
        const string TrayDir = "src/ClippysRevenge.App/Tray/";
        var ui = Scan.Value.Files.Where(f => f.Rel.StartsWith(TrayDir, StringComparison.Ordinal)
            || f.Rel == "src/ClippysRevenge.Core/AiDraftsUi.cs").ToList();
        Assert.NotEmpty(ui);
        Assert.Contains(ui, f => f.Rel.EndsWith("AiDraftsUi.cs", StringComparison.Ordinal));

        var bad = new List<string>();
        foreach (var f in ui)
            foreach (var lit in f.Literals)
            {
                var text = lit[(lit.IndexOf('|') + 1)..];
                if (Regex.IsMatch(text, @"(?i)claude\.ai|\blogin\b|/login|setup-token|oauth")) bad.Add($"{f.Rel}: \"{text}\"");
            }
        AssertNone(bad, "AI UI strings must never mention a sign-in (claude.ai, login, setup-token, oauth).");

        var trayFiles = ui.Where(f => f.Rel.StartsWith(TrayDir, StringComparison.Ordinal)).ToList();
        AssertNone(trayFiles.Where(f => Regex.IsMatch(f.Code, @"\bLoadApiKey\b|\bCachedApiKey\b")).Select(f => f.Rel).ToList(),
            "Tray code never reads the API key: only Program calls LoadApiKey.");

        var form = trayFiles.Where(f => f.Rel.StartsWith(TrayDir + "SettingsForm", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(form);
        foreach (var f in form)
            foreach (Match m in Regex.Matches(f.Code, @"_apiKeyBox\s*\.\s*Text\s*=(?!=)"))
                Assert.Matches(@"\G\s*""""\s*;", f.Code[(m.Index + m.Length)..]);
        Assert.Contains(form, f => Regex.IsMatch(f.Code, @"_apiKeyBox\s*\.\s*Text\s*=(?!=)"));

        AssertNone(trayFiles.Where(f => Regex.IsMatch(f.Code, @"_status\.Text\s*=[^;]*_apiKeyBox|Log\w*\([^;]*_apiKeyBox"))
            .Select(f => f.Rel).ToList(), "The API key box is never logged or shown in the status line.");
    }

    [Fact]
    public void ClaudeCli_StartsProcessesSafely()
    {
        var cli = Scan.Value.Files.Single(f => f.Rel == ClaudeCliFile);
        Assert.Matches(@"UseShellExecute\s*=\s*false", cli.Code);
        AssertNone(Find(f => f.Code, @"UseShellExecute\s*=\s*true", f => f.Rel == TrayFile || f.Rel == Launcher),
            "UseShellExecute = true is only allowed in TrayApp.cs and OutlookLauncher.cs.");

        var bad = new List<string>();
        var schemaFiles = new List<string>();
        foreach (var f in Scan.Value.Files)
        {
            foreach (var lit in f.Literals)
            {
                var text = lit[(lit.IndexOf('|') + 1)..];
                if (text is "--bare" or "login") bad.Add($"{f.Rel}: \"{text}\"");
                if (text == "--json-schema") schemaFiles.Add(f.Rel);
            }
        }
        AssertNone(bad, "No literal may equal --bare or login (Clippy never uses --bare and never signs in).");
        Assert.NotEmpty(schemaFiles);
        Assert.All(schemaFiles, r => Assert.Equal(ClaudeCliFile, r));
    }

    // ---- Mail items ----

    // ---- Draft invite window (M15.4, SPEC 22.3) ----

    private const string OpenInviteApp = "src/ClippysRevenge.App/OutlookCalendar.OpenInvite.cs";
    private const string DisplayRegex = @"\.\s*Display\s*\(";

    [Fact]
    public void Display_OnlyInOpenDraftInvite()
    {
        var hits = Find(f => f.Code, DisplayRegex);
        Assert.True(hits.Count == 1 && hits[0].StartsWith(OpenInviteApp + ":", StringComparison.Ordinal),
            ".Display( is allowed once, in OpenDraftInvite (SPEC 22.3); drafts are saved, never shown. Found:"
            + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void OpenDraftInvite_WritesNothing()
    {
        var f = Scan.Value.Files.Single(x => x.Rel == OpenInviteApp);
        Assert.DoesNotMatch(@"\.\s*(Save|Send|Close|Move|Delete|PermanentDelete|Copy)\s*\(", f.Code);
        Assert.DoesNotMatch(@"\b\w+\s*\.\s*\w+\s*=(?![=>])", f.Code);
        Assert.Contains("Display(false)", f.Code);
    }

    [Fact]
    public void OpenInviteFile_NeverCreatesOutlook()
    {
        var f = Scan.Value.Files.Single(x => x.Rel == OpenInviteApp);
        foreach (var bad in new[] { "new Outlook.Application", "Activator.CreateInstance", "CoCreateInstance", "GetActiveObject" })
        {
            Assert.DoesNotContain(bad, f.Code);
        }
    }

    [Fact]
    public void DisplayMatcher_CatchesPlantedViolations()
    {
        Assert.Matches(DisplayRegex, "item.Display(false);");
        Assert.Matches(DisplayRegex, "item . Display ();");
        Assert.DoesNotMatch(DisplayRegex, "x.DisplayName");
        Assert.DoesNotMatch(DisplayRegex, "Display(false);");
    }

    [Fact]
    public void NeverDeletesOutlookItems()
    {
        // Only the app's own files (index databases, logs, partial attachment saves) may be deleted.
        var hits = Find(f => f.Code, @"\b\w+\s*\.\s*(Delete|PermanentDelete|EmptyDeletedItems)\s*\(",
                f => !f.Rel.StartsWith("src/ClippysRevenge.App/", StringComparison.Ordinal))
            .Where(h => !Regex.IsMatch(h, @": (File|Directory)\s*\.")).ToList();
        AssertNone(hits, "Clippy never deletes Outlook items, folders or mail (CLAUDE.md hard rule).");
    }

    [Fact]
    public void OnlyMoveIsNewReplyDraftIntoDrafts()
    {
        var hits = Find(f => f.Code, @"\b\w+\s*\.\s*Move\s*\(\s*\w*",
                f => !f.Rel.StartsWith("src/ClippysRevenge.App/", StringComparison.Ordinal))
            .Where(h => !Regex.IsMatch(h, @": (File|Directory)\s*\.")).ToList();
        Assert.True(hits.Count == 1 && hits[0].StartsWith("src/ClippysRevenge.App/OutlookMail.Reply.cs:", StringComparison.Ordinal)
                && hits[0].EndsWith("reply.Move(drafts", StringComparison.Ordinal),
            "The only Outlook .Move( allowed is the new reply draft into Drafts, found:" + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }

    // ---- Draft invite category (M15.2, SPEC 22.2) ----

    private const string DraftInvitesApp = "src/ClippysRevenge.App/OutlookCalendar.DraftInvites.cs";
    private const string InvitesApp = "src/ClippysRevenge.App/OutlookCalendar.Invites.cs";
    private const string CategoriesAssign = @"\.\s*Categories\s*=(?![=>])";
    private const string AnyAssign = @"\b\w+\s*\.\s*(\w+)\s*=(?![=>])";

    [Fact]
    public void CategoriesWrites_OnlyInCreateMeetingAndCheck()
    {
        var hits = Find(f => f.Code, CategoriesAssign);
        Assert.Equal(2, hits.Count);
        Assert.Single(hits, h => h.StartsWith(InvitesApp + ":", StringComparison.Ordinal));
        Assert.Single(hits, h => h.StartsWith(DraftInvitesApp + ":", StringComparison.Ordinal));
    }

    [Fact]
    public void DraftInviteCheck_IsCategoriesOnlySave()
    {
        var f = Scan.Value.Files.Single(x => x.Rel == DraftInvitesApp);
        Assert.Single(Regex.Matches(f.Code, @"\.\s*Save\s*\(\s*\)"));
        var assigns = Regex.Matches(f.Code, AnyAssign);
        var m = Assert.Single(assigns);
        Assert.Equal("Categories", m.Groups[1].Value);
        Assert.DoesNotMatch(@"\.\s*(Close|Display|Send|Move|Delete|PermanentDelete)\s*\(", f.Code);
    }

    [Fact]
    public void MasterCategories_NeverRecolouredOrRemoved()
    {
        var files = Scan.Value.Files.Where(f => f.Rel.StartsWith("src/ClippysRevenge.App/OutlookCalendar", StringComparison.Ordinal)).ToList();
        Assert.Contains(files, f => f.Rel == InvitesApp);
        Assert.Contains(files, f => f.Rel == DraftInvitesApp);
        var bad = files.SelectMany(f => Regex.Matches(f.Code, @"\.\s*Color\s*=(?!=)|\.\s*Remove\s*\(")
            .Select(m => $"{f.Rel}:{LineOf(f.Code, m.Index)}: {m.Value}")).ToList();
        AssertNone(bad, "Master-list categories are never recoloured or removed.");
        Assert.Contains("OlCategoryColor.olCategoryColorOrange", files.Single(f => f.Rel == InvitesApp).Code);
    }

    [Fact]
    public void CategoryName_IsTheSpecString()
    {
        Assert.Equal("Clippy draft invite", DraftInvites.CategoryName);
        Assert.EndsWith("/8229000B", DraftInvites.FInvitedProperty);
        Assert.Contains("{00062002-0000-0000-C000-000000000046}", DraftInvites.FInvitedProperty);
    }

    [Fact]
    public void CategoryScanMatchers_CatchPlantedViolations()
    {
        Assert.Matches(CategoriesAssign, "item.Categories = x;");
        Assert.DoesNotMatch(CategoriesAssign, "if (item.Categories == x) {}");
        Assert.DoesNotMatch(CategoriesAssign, "var c = item.Categories;");
        var m = Regex.Match("item.Subject = \"x\";", AnyAssign);
        Assert.True(m.Success);
        Assert.Equal("Subject", m.Groups[1].Value);
    }

    // ---- Graph ----

    [Fact]
    public void NoGraphOrMsal()
    {
        AssertNone(Find(f => f.WithStrings, @"graph\.microsoft\.com|Microsoft\.Graph|\bMsal\w*|Microsoft\.Identity|login\.microsoftonline|\bGraphServiceClient\b",
            opts: RegexOptions.IgnoreCase), "Graph / MSAL / OAuth are forbidden.");

        var root = Scan.Value.Root;
        var bad = new List<string>();
        foreach (var p in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "Directory.Packages.props", SearchOption.AllDirectories)))
        {
            var lines = File.ReadAllLines(p);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"Microsoft\.Graph|Microsoft\.Identity|\bMsal", RegexOptions.IgnoreCase))
                {
                    bad.Add($"{Path.GetRelativePath(root, p)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }
        AssertNone(bad, "Graph / identity package references are forbidden.");
    }

    // ---- Binding ----

    [Fact]
    public void NoWildcardBinding()
    {
        AssertNone(Find(f => f.Code, @"IPAddress\s*\.\s*(Any|IPv6Any)\b|\bListenAnyIP\b|\bUseUrls\b|\bIPv6Any\b"),
            "Wildcard binding is forbidden; bind IPAddress.Loopback only.");
        AssertNone(Find(f => f.WithStrings, @"0\.0\.0\.0|\[::\]|://\*|://\+"), "Wildcard address literal in src.");
    }

    [Fact]
    public void KestrelBindsLoopback()
    {
        var host = Scan.Value.Files.Single(f => f.Rel == "src/ClippysRevenge.Core/McpHttpHost.cs");
        Assert.Matches(@"\.Listen\s*\(\s*IPAddress\s*\.\s*Loopback\s*,", host.Code);
        AssertNone(Find(f => f.Code, @"\.\s*Listen\s*\(\s*(?!IPAddress\s*\.\s*Loopback\b)[^)]*\)"),
            "Every Listen( must use IPAddress.Loopback.");
    }

    // ---- Reply subject ----

    [Fact]
    public void ReplySubjects_OnlyViaReplySubject()
    {
        var hits = new List<string>();
        foreach (var f in Scan.Value.Files)
        {
            if (f.Rel.EndsWith("/ReplySubject.cs", StringComparison.Ordinal)) continue;
            foreach (var lit in f.Literals)
            {
                var bar = lit.IndexOf('|');
                var body = lit[(bar + 1)..];
                if (Regex.IsMatch(body, @"^\s*(re|fw|fwd)\s*:", RegexOptions.IgnoreCase))
                {
                    hits.Add($"{f.Rel}:{lit[..bar]}: \"{body}\"");
                }
            }
        }
        AssertNone(hits, "Reply/forward subject prefixes may only be built by ReplySubject.Normalize.");
    }

    [Fact]
    public void ReplySubject_UsedByReplyDraftPath()
    {
        Assert.Contains(Scan.Value.Files, f => f.Rel != "src/ClippysRevenge.Core/ReplySubject.cs"
            && Regex.IsMatch(f.Code, @"ReplySubject\s*\.\s*Normalize\s*\("));
    }

    // ---- The scanner itself ----

    [Fact]
    public void Strip_IgnoresCommentsAndStrings_KeepsLineNumbers()
    {
        const string src = "// x.Send()\nvar a = \"x.Send()\"; /* y.Send()\n z.Send() */ var b = @\"q\"\"\n.Send()\"; var c = '\"';\nreal.Send();\n";
        var code = Strip(src, blankStrings: true, lits: null);
        Assert.Single(Regex.Matches(code, @"\.Send\b"));
        Assert.Equal(src.Count(ch => ch == '\n'), code.Count(ch => ch == '\n'));
        Assert.Equal(5, LineOf(code, code.IndexOf(".Send", StringComparison.Ordinal)));

        var lits = new List<string>();
        var kept = Strip(src, blankStrings: false, lits);
        Assert.DoesNotContain("y.Send", kept);
        Assert.Contains(lits, l => l == "2|x.Send()");
    }

    [Fact]
    public void AnthropicApi_UsesOnlyTheSavedKey()
    {
        const string model = "src/ClippysRevenge.Core/AnthropicApiModel.cs";
        var f = Scan.Value.Files.Single(x => x.Rel == model);
        var ctor = @"new\s+AnthropicClient\s*\(";
        Assert.Matches(ctor, f.Code);
        AssertNone(Find(x => x.Code, ctor, x => x.Rel == model), "AnthropicClient may only be built in AnthropicApiModel.cs.");
        foreach (var required in new[] { @"ApiKey\s*=", @"MaxRetries\s*=\s*0", @"BaseUrl\s*=\s*ApiBaseUrl", @"OpenTelemetry\s*=\s*false" })
            Assert.Matches(required, f.Code);
        foreach (var forbidden in new[]
        {
            @"\bAuthToken\s*=", @"\bCredentials\s*=", "AnthropicCredentials", @"\bThinking\s*=", @"\bTools\s*=",
            @"\bToolChoice\s*=", @"\bContainer\s*=", @"\.\s*Beta\b", "ANTHROPIC_",
        })
        {
            Assert.DoesNotMatch(forbidden, f.Code);
        }
        Assert.DoesNotMatch("ANTHROPIC_", f.WithStrings);
        AssertNone(Find(x => x.Code, "AnthropicCredentials", x => x.Rel == model), "AnthropicCredentials must not be used.");
    }

    // ---- AI drafting (SPEC 19.12) ----

    private static readonly Regex CredentialLiteral = new(@"\.credentials|claudeAiOauth|oauthAccount|sk-ant-oat", RegexOptions.IgnoreCase);

    private static readonly Regex AiForbiddenCall = new(@"\.\s*(Send|Display|Move)\s*\(|\bDelete\s*\(|\bPermanentDelete\s*\(");

    private static bool Hit(Regex re, string s) => re.IsMatch(s);

    internal static bool IsAiFile(string rel)
    {
        var name = Path.GetFileName(rel);
        return name.StartsWith("Draft", StringComparison.Ordinal) || name.StartsWith("Claude", StringComparison.Ordinal)
            || name.StartsWith("Style", StringComparison.Ordinal) || name.StartsWith("Anthropic", StringComparison.Ordinal)
            || name.StartsWith("OutlookAiMailbox", StringComparison.Ordinal);
    }

    /// <summary>Forbidden Outlook-style calls in AI file code; File./Directory. calls (temp files, atomic rename) are exempt.</summary>
    internal static List<string> AiForbiddenHits(string code)
    {
        var hits = new List<string>();
        foreach (Match m in AiForbiddenCall.Matches(code))
        {
            var lineStart = code.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
            // For ".Move(" the receiver precedes the match; for "Delete(" the receiver precedes a dot just before it.
            var prefix = code[lineStart..m.Index];
            if (!m.Value.StartsWith('.') && prefix.EndsWith('.')) prefix = prefix[..^1];
            if (Regex.IsMatch(prefix, @"\b(File|Directory)\s*$")) continue;
            hits.Add($"line {LineOf(code, m.Index)}: {m.Value.Trim()}");
        }
        return hits;
    }

    [Fact]
    public void AiFiles_SendDisplayMoveDelete_Absent()
    {
        var files = Scan.Value.Files.Where(f => IsAiFile(f.Rel)).ToList();
        Assert.Contains(files, f => f.Rel.EndsWith("/ClaudeCli.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Rel.EndsWith("/OutlookAiMailbox.cs", StringComparison.Ordinal));
        var hits = files.SelectMany(f => AiForbiddenHits(f.Code).Select(h => f.Rel + ": " + h)).ToList();
        AssertNone(hits, "AI files must not call .Send(, .Display(, .Move( or Delete( (File./Directory. calls are exempt).");
    }

    [Fact]
    public void AiForbiddenHits_MatcherCatchesPlantedViolations()
    {
        Assert.NotEmpty(AiForbiddenHits("item.Send();"));
        Assert.NotEmpty(AiForbiddenHits("item . Display(false);"));
        Assert.NotEmpty(AiForbiddenHits("reply.Move(drafts);"));
        Assert.NotEmpty(AiForbiddenHits("item.Delete();"));
        Assert.NotEmpty(AiForbiddenHits("Delete(x);"));
        Assert.NotEmpty(AiForbiddenHits("File.Exists(a); item.Delete();"));
        Assert.Empty(AiForbiddenHits("File.Delete(tmp); File.Move(a, b, true); Directory.Delete(d, true);"));
        Assert.Empty(AiForbiddenHits("var x = list.Remove(y);"));
        Assert.True(IsAiFile("src/A/DraftWorker.cs"));
        Assert.True(IsAiFile("src/A/OutlookAiMailbox.cs"));
        Assert.False(IsAiFile("src/A/OutlookMail.cs"));
    }

    [Fact]
    public void NoClaudeCredentialReferences_InStringLiterals()
    {
        var hits = new List<string>();
        foreach (var f in Scan.Value.Files)
        {
            hits.AddRange(f.Literals.Where(l => CredentialLiteral.IsMatch(l[(l.IndexOf('|') + 1)..])).Select(l => $"{f.Rel}:{l}"));
        }
        AssertNone(hits, "No literal may reference Claude credential stores (.credentials, claudeAiOauth, oauthAccount, sk-ant-oat).");
        Assert.True(Hit(CredentialLiteral, ".credentials.json"));
        Assert.True(Hit(CredentialLiteral, "sk-ant-oat01-abc"));
        Assert.True(Hit(CredentialLiteral, "claudeAiOauth"));
        Assert.True(Hit(CredentialLiteral, "oauthAccount"));
        Assert.False(Hit(CredentialLiteral, "sk-ant-api03-abc"));
    }

    [Fact]
    public void NoLiteralBareOrLogin_MatcherCatchesPlantedViolations()
    {
        var lits = new List<string>();
        Strip("var a = \"--bare\"; var b = \"login\"; var c = \"--json-schema\";", blankStrings: false, lits);
        var texts = lits.Select(l => l[(l.IndexOf('|') + 1)..]).ToList();
        Assert.Contains("--bare", texts);
        Assert.Contains("login", texts);
        Assert.Contains("--json-schema", texts);
    }

    [Fact]
    public void ModelGetsNoTools_ClaudeCliArgsAndApiCall()
    {
        var cli = Scan.Value.Files.Single(f => f.Rel == ClaudeCliFile);
        var lits = cli.Literals.Select(l => l[(l.IndexOf('|') + 1)..]).ToList();
        Assert.Contains("--tools", lits);
        Assert.DoesNotContain(lits, l => l is "--mcp-config" or "--allowedTools" or "--dangerously-skip-permissions");
        var api = Scan.Value.Files.Single(f => f.Rel == "src/ClippysRevenge.Core/AnthropicApiModel.cs");
        Assert.DoesNotMatch(@"\bTools\s*=|\bToolChoice\s*=|\bMcpServers\b", api.Code);
        Assert.Matches(@"\bTools\s*=", "Tools = x");
    }

    [Fact]
    public void AiOutput_ReachesOutlookOnlyThroughReplyDraftWriter()
    {
        // The model classes never touch COM; only OutlookAiMailbox (App) calls the reply-draft path.
        var comTypes = new Regex(@"\bOutlook\.|\bMarshal\b|\bMailItem\b|CreateReplyDraft");
        var core = Scan.Value.Files.Where(f => f.Rel.StartsWith("src/ClippysRevenge.Core/", StringComparison.Ordinal) && IsAiFile(f.Rel));
        var hits = core.Where(f => comTypes.IsMatch(f.Code)).Select(f => f.Rel).ToList();
        AssertNone(hits, "AI files in Core must not reference COM types or CreateReplyDraft.");
        Assert.True(Hit(comTypes, "x.CreateReplyDraft()"));
    }

    // ---- Log safety (SPEC 5.1, 11.11, 11.12) ----

    private static readonly Regex LogCall = new(@"\.Log(Trace|Debug|Information|Warning|Error|Critical)?\s*\(");
    private static readonly Regex LogArgBad = new(
        @"\b(sql|query|sender|recipient|subject|body|folder|date_from|date_to|attachment_name|attachment_ext|mailbox|group_by|order|text|id)\b|\.Rows\b|\.Columns\b(?!\.Count\b)");

    internal static List<string> LogArgumentHits(string code, string rel)
    {
        var hits = new List<string>();
        foreach (Match m in LogCall.Matches(code))
        {
            var end = code.IndexOf(';', m.Index);
            var call = code[m.Index..(end < 0 ? code.Length : end)];
            if (LogArgBad.IsMatch(call))
            {
                hits.Add($"{rel}:{LineOf(code, m.Index)}: {call.Trim()}");
            }
        }
        return hits;
    }

    [Fact]
    public void IndexQueryAndEmailStats_NeverLog()
    {
        foreach (var rel in new[] { "src/ClippysRevenge.Core/IndexQuery.cs", "src/ClippysRevenge.Core/EmailStats.cs" })
        {
            var f = Scan.Value.Files.SingleOrDefault(x => x.Rel == rel);
            Assert.NotNull(f);
            Assert.DoesNotMatch(@"\bILogger\b|\bLog(Trace|Debug|Information|Warning|Error|Critical)?\s*\(|\bConsole\.|\bDebug\.Write|\bTrace\.Write", f!.Code);
        }
    }

    [Fact]
    public void McpToolsLogCalls_NeverTakeSqlSearchTextOrCells()
    {
        var files = Scan.Value.Files.Where(f => f.Rel.StartsWith("src/ClippysRevenge.App/McpTools", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(files);
        var hits = files.SelectMany(f => LogArgumentHits(f.Code, f.Rel)).ToList();
        AssertNone(hits, "Log calls in McpTools*.cs must not take SQL, search text, cell values or column names.");
        var index = files.Single(f => f.Rel.EndsWith("McpTools.Index.cs", StringComparison.Ordinal));
        Assert.True(LogCall.Matches(index.Code).Count >= 2);
    }

    [Fact]
    public void LogArgumentMatcher_CatchesPlantedViolations()
    {
        Assert.Single(LogArgumentHits("log.LogDebug(\"\", sql);", "x"));
        Assert.Single(LogArgumentHits("log.LogDebug(\"\", r.Rows[0][1]);", "x"));
        Assert.Single(LogArgumentHits("log.LogDebug(\"\", r.Columns);", "x"));
        Assert.Single(LogArgumentHits("log.LogError(ex, \"\", query);", "x"));
        Assert.Empty(LogArgumentHits("log.LogDebug(\"\", r.RowCount, r.Columns.Count, MailboxLabel(key), r.ElapsedMs);", "x"));
    }

    [Fact]
    public void IndexTools_ReadOnly_CoreTexts_NoCom()
    {
        var f = Scan.Value.Files.Single(x => x.Rel.EndsWith("McpTools.Index.cs", StringComparison.Ordinal));
        foreach (var s in new[]
        {
            "Name = \"email_stats\", ReadOnly = true, Idempotent = true",
            "Name = \"query_index\", ReadOnly = true, Idempotent = true",
            "[Description(EmailStats.ToolDescription)]",
            "[Description(IndexQueryTexts.ToolDescription)]",
        })
        {
            Assert.Contains(s, f.WithStrings);
        }
        Assert.DoesNotMatch(@"\bworker\.|\bstores\.|\bRunAsync\b|\bEnsureResolvedAsync\b|\bOutlookMail\b", f.Code);
    }
}
