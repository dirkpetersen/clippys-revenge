using System.Text;

namespace ClippysRevenge.Core.Tests;

internal enum FakeBehavior { Echo, Sleep, Exit3, BigStdout }

/// <summary>Writes a fake `claude` script (never the real CLI) and builds the command that runs it.</summary>
internal static class FakeClaude
{
    public const string Envelope =
        """{"type":"result","subtype":"success","is_error":false,"result":"","usage":{"input_tokens":120,"output_tokens":45},"structured_output":{"needs_reply":true,"confidence":0.8,"reason":"x","body_html":"<p>Hi</p>"}}""";

    public static ClaudeCliCommand Create(string dir, FakeBehavior behavior)
    {
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, "fake.ps1");
            File.WriteAllText(path, Ps(behavior), new UTF8Encoding(false));
            return new ClaudeCliCommand("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path]);
        }
        var sh = Path.Combine(dir, "fake.sh");
        File.WriteAllText(sh, Sh(behavior).Replace("\r\n", "\n"), new UTF8Encoding(false));
        return new ClaudeCliCommand("sh", [sh]);
    }

    private static string Sh(FakeBehavior b) => b switch
    {
        FakeBehavior.Echo => """
            d="$(dirname "$0")"
            pwd > "$d/pwd.txt"
            : > "$d/args.txt"
            prev=""
            for a in "$@"; do
              printf '%s\n' "$a" >> "$d/args.txt"
              if [ "$prev" = "--system-prompt-file" ] && [ -f "$a" ]; then
                cp "$a" "$d/sp-copy.md"
                echo yes > "$d/sp-exists.txt"
              fi
              prev="$a"
            done
            cat > "$d/stdin.txt"
            printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"","usage":{"input_tokens":120,"output_tokens":45},"structured_output":{"needs_reply":true,"confidence":0.8,"reason":"x","body_html":"<p>Hi</p>"}}'
            echo oops >&2
            exit 0
            """,
        FakeBehavior.Sleep => """
            d="$(dirname "$0")"
            echo $$ > "$d/pid.txt"
            sleep 60
            """,
        FakeBehavior.Exit3 => """
            printf 'partial'
            echo 'bad thing' >&2
            exit 3
            """,
        _ => """
            head -c 5000000 /dev/zero | tr '\0' 'a'
            """,
    };

    private static string Ps(FakeBehavior b) => b switch
    {
        FakeBehavior.Echo => """
            [Console]::InputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $d = $PSScriptRoot
            $enc = [Text.UTF8Encoding]::new($false)
            [IO.File]::WriteAllText("$d\pwd.txt", (Get-Location).Path, $enc)
            $prev = ''
            foreach ($a in $args) {
              if ($prev -eq '--system-prompt-file' -and (Test-Path -LiteralPath $a)) {
                Copy-Item -LiteralPath $a -Destination "$d\sp-copy.md"
                [IO.File]::WriteAllText("$d\sp-exists.txt", 'yes', $enc)
              }
              $prev = $a
            }
            $in = [Console]::In.ReadToEnd()
            [IO.File]::WriteAllText("$d\stdin.txt", $in, $enc)
            [Console]::Out.WriteLine('{"type":"result","subtype":"success","is_error":false,"result":"","usage":{"input_tokens":120,"output_tokens":45},"structured_output":{"needs_reply":true,"confidence":0.8,"reason":"x","body_html":"<p>Hi</p>"}}')
            [Console]::Error.Write('oops')
            exit 0
            """,
        FakeBehavior.Sleep => """
            [IO.File]::WriteAllText("$PSScriptRoot\pid.txt", "$PID")
            Start-Sleep -Seconds 60
            """,
        FakeBehavior.Exit3 => """
            [Console]::Out.Write('partial')
            [Console]::Error.Write('bad thing')
            exit 3
            """,
        _ => """
            [Console]::Out.Write('a' * 5000000)
            """,
    };
}
