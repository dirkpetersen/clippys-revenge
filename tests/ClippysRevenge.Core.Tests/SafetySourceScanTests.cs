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
    private const string ClaudeCliFile = "src/ClippysRevenge.Core/ClaudeCli.cs";

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
        AssertNone(Find(f => f.Code, @"\bProcess\s*\.\s*Start\b|\bShellExecute\w*\s*\(", f => f.Rel == Launcher || f.Rel == TrayFile || f.Rel == ClaudeCliFile),
            "Only OutlookLauncher.cs, TrayApp.cs (Open data folder) and ClaudeCli.cs may start a process.");
        // TrayApp.OpenFolder opens a folder in Explorer (not Outlook); it is the only other Process.Start.
        var tray = Scan.Value.Files.Single(f => f.Rel == TrayFile);
        Assert.Single(Regex.Matches(tray.Code, @"\bProcess\s*\.\s*Start\b"));
        Assert.Matches(@"void\s+OpenFolder\s*\(string path\)\s*\{[^}]*Process\.Start\(new ProcessStartInfo\(path\)", tray.Code);

        var l = Scan.Value.Files.SingleOrDefault(f => f.Rel == Launcher);
        Assert.NotNull(l);
        Assert.Matches(@"Process\s*\.\s*Start\s*\(", l!.Code);
        Assert.DoesNotMatch(@"Marshal\b|Outlook\.|GetActiveObject|Activator|ProgID", l.Code);
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

    [Fact]
    public void NoDisplayCalls() =>
        AssertNone(Find(f => f.Code, @"\.\s*Display\s*\("), ".Display( is forbidden: drafts are saved, never shown.");

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
    }}


