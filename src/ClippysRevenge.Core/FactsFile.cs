using System.Text;
using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

/// <summary>SPEC 25.2 / 5.1. Both fully qualified.</summary>
public sealed record FactsPaths(string File, string StateFile)
{
    public const string SkillName = "my-email-context";
    public const string StateFileName = "facts-state.json";

    /// <param name="userProfileDir">%USERPROFILE%.</param>
    /// <param name="appDataDir">%APPDATA%\ClippysRevenge.</param>
    /// <returns>File = userProfileDir\.claude\skills\my-email-context\SKILL.md;
    /// StateFile = appDataDir\my-email-style\facts-state.json (next to the style skill's state.json).</returns>
    /// <exception cref="ArgumentException">Either is null, blank or not fully qualified.</exception>
    public static FactsPaths Default(string userProfileDir, string appDataDir)
    {
        if (string.IsNullOrWhiteSpace(userProfileDir) || !Path.IsPathFullyQualified(userProfileDir))
            throw new ArgumentException("The user profile folder must be a fully qualified path.", nameof(userProfileDir));
        if (string.IsNullOrWhiteSpace(appDataDir) || !Path.IsPathFullyQualified(appDataDir))
            throw new ArgumentException("The app data folder must be a fully qualified path.", nameof(appDataDir));
        return new FactsPaths(
            Path.Combine(userProfileDir, ".claude", "skills", SkillName, "SKILL.md"),
            Path.Combine(appDataDir, StyleSkillText.SkillName, StateFileName));
    }
}

public enum FactsSource { File, Empty, None }

/// <summary>Body: the cleaned text (""), never null. Only File has a non-empty Body.</summary>
public sealed record FactsPromptText(string Body, FactsSource Source);

public static class FactsTemplate
{
    /// <summary>The template text, "\n" line endings, ending with "\n". displayName goes through
    /// StyleSkillText.CleanDisplayName (null/blank gives "the user"). Never throws.</summary>
    public static string Render(string? displayName)
    {
        var name = StyleSkillText.CleanDisplayName(displayName);
        return string.Join("\n", new[]
        {
            "---",
            "name: " + FactsPaths.SkillName,
            "description: \"Facts about " + name + " for writing their email: role and team, meeting links, office hours, standard answers, who people are, and how they want replies written. Use when drafting or replying to email as " + name + ".\"",
            "---",
            "",
            "<!-- Facts about you, for Clippy's Revenge and Claude Code. Clippy adds what you write here to every AI draft request it sends to Claude, and Claude Code reads it when it writes email for you. Write short facts under the headings, one per line. Text between these comment markers is ignored, so you can leave the examples. Clippy never changes this file once it exists. Don't put passwords or other secrets here. -->",
            "",
            "## Me",
            "",
            "<!-- Your role, your team and what you work on. -->",
            "<!-- e.g. I lead the research computing team; we run the HPC cluster and research storage. -->",
            "",
            "## Links",
            "",
            "<!-- Links you often send. -->",
            "<!-- e.g. My Zoom room: https://zoom.us/my/example -->",
            "",
            "## Office hours",
            "",
            "<!-- When and how people can reach you. -->",
            "<!-- e.g. Office hours Tuesdays 2-4 pm, on Zoom or in my office. -->",
            "",
            "## Standard answers",
            "",
            "<!-- Answers you give again and again. -->",
            "<!-- e.g. New accounts: please fill in the request form at https://example.org/accounts -->",
            "",
            "## People",
            "",
            "<!-- Who people are and how you know them, one per line. -->",
            "<!-- e.g. Ricky, Dave: ski group -->",
            "",
            "## How I want replies written",
            "",
            "<!-- Preferences for your drafts; these win over the writing-style skill. -->",
            "<!-- e.g. Sign off with just my first name. Keep replies to my team short. -->",
            "",
        });
    }
}

public static class FactsTexts
{
    public const string SourceNone =
        "Facts about you: my-email-context SKILL.md not found or unreadable — drafts go without it. Use \"Edit my facts…\" in the AI drafts menu.";
    public const string SourceBack = "Facts about you: using my-email-context SKILL.md again.";

    /// <summary>For the Settings line (SPEC 25.2).</summary>
    public static string Status(FactsSource source) => source switch
    {
        FactsSource.File => "Facts about you: filled in",
        FactsSource.Empty => "Facts about you: template only",
        _ => "Facts about you: not found",
    };
}

public enum FactsTemplateResult
{
    Created,        // this call created the file
    AlreadyExists,  // a file (or a racing writer's file) was already there; untouched
    AlreadyDone,    // automatic path only: facts-state.json exists, nothing attempted
    NoConsent,      // starter only
    NameUnknown,    // starter only: owner's display name not available yet
    Busy,           // starter only: another run in progress
    Failed,         // I/O or access error; ErrorType set
}

public sealed record FactsTemplateOutcome(FactsTemplateResult Result, string? ErrorType = null);

/// <summary>The user's facts file (SPEC 25.2): read fresh for each prompt, created once from a template, never overwritten.</summary>
public sealed class FactsFile
{
    /// <summary>SPEC 25.2: files larger than this are not read (Source None).</summary>
    public const int MaxReadBytes = 16 * 1024;

    private static readonly Regex Comment = new(@"<!--[\s\S]*?-->", RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})(?:[ \t]|$)", RegexOptions.CultureInvariant);

    private readonly TimeProvider _time;

    /// <exception cref="ArgumentNullException">paths is null.</exception>
    /// <exception cref="ArgumentException">A path is blank or not fully qualified.</exception>
    public FactsFile(FactsPaths paths, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(paths.File) || !Path.IsPathFullyQualified(paths.File)
            || string.IsNullOrWhiteSpace(paths.StateFile) || !Path.IsPathFullyQualified(paths.StateFile))
            throw new ArgumentException("The facts file paths must be fully qualified.", nameof(paths));
        Paths = paths;
        _time = time ?? TimeProvider.System;
    }

    public FactsPaths Paths { get; }

    /// <summary>Read fresh on every call, never cached. Never throws, never null.</summary>
    public FactsPromptText ReadForPrompt()
    {
        try
        {
            if (!System.IO.File.Exists(Paths.File)) return new FactsPromptText("", FactsSource.None);
            if (new FileInfo(Paths.File).Length > MaxReadBytes) return new FactsPromptText("", FactsSource.None);
            byte[] buf;
            int n;
            using (var fs = new FileStream(Paths.File, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                buf = new byte[MaxReadBytes + 1];
                n = 0;
                while (n < buf.Length)
                {
                    var r = fs.Read(buf, n, buf.Length - n);
                    if (r == 0) break;
                    n += r;
                }
            }
            if (n > MaxReadBytes) return new FactsPromptText("", FactsSource.None);
            var start = n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF ? 3 : 0;
            var text = new UTF8Encoding(false, true).GetString(buf, start, n - start);
            var body = Clean(text);
            return body.Length == 0 ? new FactsPromptText("", FactsSource.Empty) : new FactsPromptText(body, FactsSource.File);
        }
        catch (Exception)
        {
            return new FactsPromptText("", FactsSource.None);
        }
    }

    /// <summary>Frontmatter, HTML comments, control characters, trailing spaces, empty headings and blank runs removed. Pure.
    /// Never throws (null gives "").</summary>
    public static string Clean(string? fileText)
    {
        try
        {
            if (string.IsNullOrEmpty(fileText)) return "";
            var text = StyleSkillText.StripHeader(fileText);
            text = Comment.Replace(text, "");
            var lines = new List<string>();
            foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
            {
                var sb = new StringBuilder(raw.Length);
                foreach (var c in raw)
                    if (!char.IsControl(c) || c == '\t') sb.Append(c);
                lines.Add(sb.ToString().TrimEnd());
            }

            // Drop headings with nothing under them, bottom-up over the kept lines.
            var kept = new List<string>(lines.Count);
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var line = lines[i];
                var m = Heading.Match(line);
                if (m.Success)
                {
                    var level = m.Groups[1].Length;
                    string? next = null;
                    for (int j = kept.Count - 1; j >= 0; j--)
                        if (kept[j].Length > 0) { next = kept[j]; break; }
                    var nm = next is null ? null : Heading.Match(next);
                    if (next is null || (nm!.Success && nm.Groups[1].Length <= level)) continue;
                }
                kept.Add(line);
            }
            kept.Reverse();

            var outLines = new List<string>(kept.Count);
            foreach (var l in kept)
            {
                if (l.Length == 0 && outLines.Count > 0 && outLines[^1].Length == 0) continue;
                outLines.Add(l);
            }
            return string.Join("\n", outLines).Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>True when Paths.StateFile exists (content not checked). False on any error.</summary>
    public bool TemplateRecorded()
    {
        try { return System.IO.File.Exists(Paths.StateFile); }
        catch (Exception) { return false; }
    }

    /// <summary>Automatic path: AlreadyDone when TemplateRecorded(); else as CreateIfMissing. Never throws.</summary>
    public FactsTemplateOutcome CreateTemplateOnce(string? displayName)
    {
        try
        {
            if (TemplateRecorded()) return new FactsTemplateOutcome(FactsTemplateResult.AlreadyDone);
            return CreateIfMissing(displayName);
        }
        catch (Exception ex)
        {
            return new FactsTemplateOutcome(FactsTemplateResult.Failed, ex.GetType().Name);
        }
    }

    /// <summary>"Edit my facts…" path: ignores the state file. Created, AlreadyExists or Failed. Never throws.</summary>
    public FactsTemplateOutcome CreateIfMissing(string? displayName)
    {
        FactsTemplateOutcome outcome;
        try
        {
            outcome = CreateFile(displayName);
        }
        catch (Exception ex)
        {
            return new FactsTemplateOutcome(FactsTemplateResult.Failed, ex.GetType().Name);
        }
        if (outcome.Result is FactsTemplateResult.Created or FactsTemplateResult.AlreadyExists)
            WriteState();
        return outcome;
    }

    private FactsTemplateOutcome CreateFile(string? displayName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.File)!);
        FileStream fs;
        try
        {
            fs = new FileStream(Paths.File, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (System.IO.File.Exists(Paths.File))
        {
            return new FactsTemplateOutcome(FactsTemplateResult.AlreadyExists);
        }
        try
        {
            using (fs)
            {
                var bytes = new UTF8Encoding(false).GetBytes(FactsTemplate.Render(displayName));
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            return new FactsTemplateOutcome(FactsTemplateResult.Created);
        }
        catch (Exception ex)
        {
            // Half-written: this call created the file just now, so removing it is safe.
            try { System.IO.File.Delete(Paths.File); }
            catch (Exception) { /* best effort */ }
            return new FactsTemplateOutcome(FactsTemplateResult.Failed, ex.GetType().Name);
        }
    }

    private void WriteState()
    {
        try
        {
            if (System.IO.File.Exists(Paths.StateFile)) return;
            var json = "{\"templateCreatedUtc\":\""
                + _time.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"}";
            StyleSkillFiles.WriteAtomic(Paths.StateFile, Encoding.UTF8.GetBytes(json));
        }
        catch (Exception)
        {
            // The next start sees the file and records it as AlreadyExists.
        }
    }
}
