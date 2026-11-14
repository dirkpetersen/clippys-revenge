# Security model

Clippy's Revenge gives an AI tool access to your mail, so it is built to do as
little as possible and to keep that access on your PC.

## What it never does

- **Never sends anything.** Not mail and not meetings. Clippy has no send call at
  all. Mail drafts are only saved, and a [meeting draft](calendar.md#meeting-drafts)
  is saved unsent in your calendar. You send both from Outlook. A test scans the
  source to make sure it stays that way.
- **Never deletes or moves email.** The only move is putting a reply draft it just
  created into Drafts. Retention and reconcile remove rows from Clippy's own index
  only, never from your mailbox.
- **Never starts Outlook** on its own. It attaches to a running Outlook. The
  tray's **Start Outlook** item, which you click yourself, is the only exception.
- **Never uses the cloud.** No Microsoft Graph, no Entra app registrations, no
  OAuth. Nothing leaves your PC except what Claude Code sends to its model, update checks to GitHub, and, while AI drafts are on, what Clippy sends to Anthropic (see [Outbound connections](#outbound-connections)).
- **Never indexes attachment contents.** Only file names.
- **Never touches your Claude credentials.** [AI drafts](ai-drafts.md) run your own signed-in Claude Code, or an API key you type in. Clippy never reads Claude Code's stored sign-in.

## Network

- **Localhost only.** The server binds to `127.0.0.1` and nothing else. Other
  computers on your network can't reach it. It does not bind to the WSL virtual
  adapter either, which is why [WSL needs mirrored networking](connect.md#from-wsl).
- **Bearer token.** Every request must carry `Authorization: Bearer <token>`. The
  comparison is constant-time. Requests without a valid token get an empty `401`.
- **Token storage.** The token is stored encrypted with Windows DPAPI for your
  Windows user, in `%APPDATA%\ClippysRevenge\token.bin`. It is never written to
  `settings.json` or to logs. Regenerate it any time from the tray. If you save an Anthropic API key for AI drafts, it is stored the same way (DPAPI-encrypted) in `%APPDATA%\ClippysRevenge\anthropic-key.bin`; the file exists only if you save a key.
- **Browsers are blocked.** Any request with an `Origin` header gets a `401`, so a
  web page can't call the server, even from your own PC.
- **No OAuth prompts.** The `401` carries no `WWW-Authenticate` header, so MCP
  clients don't start an OAuth flow.

## Outbound connections

Clippy makes exactly two kinds of outbound connection:

- **GitHub, for updates.** Once a week (unless you set **Don't check**), the
  installed copy asks GitHub for the latest release and may download
  `ClippysRevenge.exe` and `SHA256SUMS.txt`. These are plain unauthenticated
  requests that send only a `ClippysRevenge/<version>` user agent. Nothing about
  you, your mail, the index or your settings is sent, and there is no telemetry.
  See [Updates](updates.md#what-is-sent-to-github).
- **Anthropic, only while AI drafts are on.** See [AI drafts data flow](#ai-drafts-data-flow).

All connections use HTTPS with normal certificate checks. Update downloads follow
redirects only to GitHub's own hosts.

## Update trust model

An update is accepted when it comes over HTTPS from the project's GitHub release
and its SHA-256 matches the `SHA256SUMS.txt` from the same release. This proves the
file is the one published in the release and arrived intact. It does **not** prove
who published it: whoever can publish a release in the repository (a stolen account
or token, a compromised workflow) could publish a matching exe, and copies set to
**Install automatically** would install it within a week without asking. Clippy
starts only its own installed exe after an update, never runs a script, and rolls
back a version that doesn't start.

If you want to review each release first, set Settings, General, **Updates** to
**Only tell me** or **Don't check**. See [Updates](updates.md#trust-model).

## File access

Every file path a tool reads or writes goes through a path policy. A path must
be inside an allowed folder:

- the attachment folder, `%USERPROFILE%\Downloads\Clippy's Revenge` by default,
  which is always allowed
- `%USERPROFILE%\Documents`
- any folders you add in Settings, General

Paths with `..`, UNC paths, `\\?\` paths, alternate data streams and junction
escapes are refused. This stops a prompt-injected email from making Claude write
into places like the Startup folder, or read files such as `~/.ssh`. A refused path
is shown as `Blocked` in the Activity window.

When a meeting draft has been sent, Clippy changes one thing on it: it removes its own
**Clippy draft invite** category. It stores only the meeting's Outlook id in
`draft-invites.json`, not its subject, times or attendees.

## Index at rest

The index holds message bodies as plain text, like Outlook's own cache (OST) file.
It is protected by your Windows profile's file permissions and by your disk
encryption. There is no extra encryption layer.

!!! warning "Turn on BitLocker"
    Without full-disk encryption, anyone with your disk can read the index.

## AI drafts data flow

[AI drafts](ai-drafts.md) are the one feature that sends mail text off your PC. They
are off by default and need your consent. While on, Clippy sends to Anthropic (through
your Claude Code or your API key):

- the email being answered, with its quoted history (cut at 20,000 characters)
- up to 8 earlier messages of the same thread from the local index, your own text only
- attachment **file names**, never attachment contents
- your name and address, today's date, and your writing-style guide
- once a week, about 150 to 300 samples of your sent mail for the style guide, with
  addresses, links and phone numbers removed

Nothing else from the index is sent, and shared mailboxes only if you turn them on
one by one. The model gets **no tools**: it can't read files, search mail or run
anything. Its answer is validated and cleaned to plain formatting and saved as a
reply draft, never sent. Claude Code (or the API) and your Claude plan terms govern
what happens to the data after it leaves Clippy.

Clippy never reads Claude Code's stored credentials and never signs in for you.
Logs and the Activity window don't contain mail text, prompts, the style guide or
Claude's answers.

!!! warning "The style skill is visible to Claude Code"
    The style guide is saved as %USERPROFILE%\.claude\skills\my-email-style\SKILL.md,
    a user-level Claude Code skill, so your interactive Claude Code sessions (which
    have tools) can load it. Review it with **View** in Settings, edit it, or delete
    it any time. See [Your writing style](ai-drafts.md#your-writing-style).

## Statistics and SQL queries

[`email_stats`](tools.md#email_stats) and [`query_index`](tools.md#query_index) let
Claude count and query the local index. They expose **the same data** that
`search_emails` and `get_message` already return, and nothing else: no sync state,
settings, AI decisions or internal ids, and no attachment content (only file names
are indexed). `email_stats` returns counts and names, never mail text.

`query_index` is read-only through several independent layers, so that a mistaken or
malicious query can't change anything:

- The connection to the index file is opened **read-only**, with writing switched
  off.
- Only three views (`mail`, `mail_fts`, `mail_attachments`) can be read. Every table,
  column and function access is checked, and anything else is refused, such as other
  tables, `PRAGMA`, `ATTACH`, loading extensions and every write.
- One `SELECT` per call, no `?` parameters, a 10 second limit, at most 1000 rows, and
  long text is cut at 2,000 characters.
- The views are created for that one call and leave the index file unchanged.

It can't touch your mailbox in Outlook at all: it only reads Clippy's own index.

The text of a query (SQL), the values it returns and SQLite's error messages are
**never written to the log**. See [Logs](#logs).

## Prompt injection

Email is untrusted text, and Claude reads it. Clippy limits what a malicious email
could make Claude do: it can't send mail, can't delete mail, can only write files to
allowed folders, and every meeting stays unsent until you send it yourself. AI drafts get a model with no tools at all, so the worst a malicious email can do there is produce a bad draft. Still, check drafts
before you send them, and read a meeting draft before you click **Send**.

## Logs

Logs are in `%LOCALAPPDATA%\ClippysRevenge\logs` and kept for 7 days. They contain
tool names, counts and errors. The token, the saved API key and any `sk-ant-…` or `Bearer …` text are masked, and mail bodies and search
text are not logged. Activity events show subjects and counts only. The SQL of a `query_index` call and the text of a tool's error message (which can quote a search or a SQL error back to Claude) are not logged either, only the kind of error. Tool calls appear as the tool name, with counts and timings.

## Unsigned executable

The release exe is not code-signed yet. The installer and the automatic updater check
its SHA-256 against `SHA256SUMS.txt` from the same release before installing. That
proves integrity, not the publisher. See [Update trust model](#update-trust-model).
