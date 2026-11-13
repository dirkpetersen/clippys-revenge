using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace ClippysRevenge.Core.Tests;

/// <summary>
/// Source scan of the hard rules (SPEC section 1, CLAUDE.md): one Send, no Outlook launch via COM,
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

    // ---- Send ----

    [Fact]
    public void ExactlyOneSend_InCreateMeeting_UnderInviteActionSend()
    {
        var hits = Find(f => f.Code, @"\.\s*Send\s*\(");
        Assert.True(hits.Count == 1, "Expected exactly one .Send in src, found:" + Environment.NewLine + string.Join(Environment.NewLine, hits));

        var file = Scan.Value.Files.Single(f => Regex.IsMatch(f.Code, @"\.\s*Send\s*\("));
        Assert.Matches(@"^src/ClippysRevenge\.App/OutlookCalendar[^/]*\.cs$", file.Rel);
        Assert.Matches(@"\.Send\s*\(\s*\)\s*;", file.Code);

        var idx = Regex.Match(file.Code, @"\.\s*Send\s*\(").Index;
        var where = $"{file.Rel}:{LineOf(file.Code, idx)}";
        var before = file.Code[..idx];
        var decl = Regex.Matches(before, @"\bCreateMeeting\s*\(");
        Assert.True(decl.Count > 0, $"{where}: .Send is not inside CreateMeeting.");
        // No other member declaration may start between CreateMeeting and the Send.
        var afterDecl = before[decl[^1].Index..];
        Assert.DoesNotMatch(@"\n\s*(public|private|internal|protected)\s[^;={]*\([^;]*\)\s*\n\s*\{", afterDecl[1..]);

        var cases = Regex.Matches(before, @"\b(case\s+[^:\n]+:|default\s*:)");
        Assert.True(cases.Count > 0, $"{where}: .Send is not under a switch case.");
        var lastCase = cases[^1].Value;
        Assert.True(lastCase.Replace(" ", "") == "caseInviteAction.Send:",
            $"{where}: .Send must sit directly under 'case InviteAction.Send:', found '{lastCase}'.");
    }

    [Fact]
    public void Send_IsOnlyReachedViaInviteGateDecision()
    {
        var file = Scan.Value.Files.Single(f => f.Rel.EndsWith("OutlookCalendar.Invites.cs", StringComparison.Ordinal));
        Assert.Matches(@"switch\s*\(\s*decision\.Action\s*\)", file.Code);
        Assert.Matches(@"decision\.Action\s+is\s+not\s*\(\s*InviteAction\.Send\s+or\s+InviteAction\.SaveDraft\s*\)", file.Code);
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
        AssertNone(Find(f => f.Code, @"\bProcess\s*\.\s*Start\b|\bShellExecute\w*\s*\(", f => f.Rel == Launcher || f.Rel == TrayFile),
            "Only OutlookLauncher.cs may start a process.");
        // TrayApp.OpenFolder opens a folder in Explorer (not Outlook); it is the only other Process.Start.
        var tray = Scan.Value.Files.Single(f => f.Rel == TrayFile);
        Assert.Single(Regex.Matches(tray.Code, @"\bProcess\s*\.\s*Start\b"));
        Assert.Matches(@"void\s+OpenFolder\s*\(string path\)\s*\{[^}]*Process\.Start\(new ProcessStartInfo\(path\)", tray.Code);

        var l = Scan.Value.Files.SingleOrDefault(f => f.Rel == Launcher);
        Assert.NotNull(l);
        Assert.Matches(@"Process\s*\.\s*Start\s*\(", l!.Code);
        Assert.DoesNotMatch(@"Marshal\b|Outlook\.|GetActiveObject|Activator|ProgID", l.Code);
    }

    // ---- Mail items ----

    [Fact]
    public void NoDisplayCalls() =>
        AssertNone(Find(f => f.Code, @"\.\s*Display\s*\("), ".Display( is forbidden: drafts are saved, never shown.");

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
}
