# Security model

Clippy's Revenge gives an AI tool access to your mail, so it is built to do as
little as possible and to keep that access on your PC.

## What it never does

- **Never sends anything.** Not mail and not meetings. Clippy has no send call at
  all. Mail drafts are only saved, and a [meeting draft](calendar.md#meeting-drafts)
  is saved unsent in your calendar. You send both from Outlook. A test scans the
  source to make sure it stays that way.
- **Never deletes email.** Clippy never calls delete, permanent delete or "empty
  Deleted Items", and never moves existing mail. There are exactly **two narrow
  exceptions**, both about Clippy's own drafts:
    1. a reply draft Clippy has just created is moved into **Drafts**;
    2. an **outdated AI draft** of Clippy's own is moved into **Deleted Items**
       (not deleted, so you can still recover it). This happens only when the draft
       carries Clippy's AI marker, is unchanged since Clippy saved it, is unsent,
       is still in Drafts, is not open in a window, and you haven't replied in the
       thread. The checks run again right before the move. See
       [Outdated drafts](ai-drafts.md#outdated-drafts).

    Nothing is ever permanently deleted and Deleted Items is never emptied.
    Retention and reconcile remove rows from Clippy's own index only, never from your
    mailbox. A test scans the source to make sure it stays that way.
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

## Attachment download links

The [`attachment_link`](tools.md#attachment_link) tool lets a shell, usually Claude Code
in WSL, download one attachment as a file. It adds one endpoint, `GET /files/<link
token>`, on the same server. Everything in [Network](#network) applies to it unchanged:
loopback only, constant-time bearer check, no browsers.

- **Two secrets are needed.** The request must carry your bearer token **and** the
  link token. The link appears in the Claude Code conversation, so a leaked link
  alone is useless. Anything else on your PC that can reach `127.0.0.1` (another
  Windows user, other WSL processes) can't use it without the bearer token either.
  The token reaches the shell through `CLIPPY_TOKEN`, set once by you (see
  [Connect](connect.md#set-clippy_token-for-downloads)). It is never put in a tool
  result.
- **Link token.** 32 random bytes, 43 characters (`A-Z a-z 0-9 - _`). It is kept in
  memory only, never written to disk, never shown in Activity and never logged.
- **One download, 10 minutes.** The link is removed before the first byte is sent.
  A second request gets `404`. A request that fails the bearer check (`401`) never
  reaches the link, so it doesn't use it up. After 10 minutes the link is gone.
- **Private folder.** Clippy saves the attachment into
  `%LOCALAPPDATA%\ClippysRevenge\links\` when the link is made. The folder is
  created for your Windows user only: no inherited permissions, full control for
  your user account alone. Files there are named with 32 random hex characters and
  `.bin`, never the attachment's name. If the folder is a junction or symlink, or its
  permissions can't be set, `attachment_link` refuses.
- **No copies left behind.** The file is deleted right after its download ends
  (completed or interrupted), when the link expires (checked every 60 seconds), when
  the app exits, when the port changes or the server restarts, and at app start,
  when any leftover `<32 hex>.bin` file in that folder is removed. A file that can't
  be deleted is retried at the next check. Other files in the folder are left alone.
- **Limits.** 100 MiB per attachment, 10 open links, 200 MiB open in total and 20
  links per rolling 10 minutes. See [the limits](tools.md#attachment_link-limits).
- **Nothing is indexed.** Attachment content never enters the index, and the link
  code doesn't touch the index at all. A test checks this.
- **No path from the model.** The model chooses an email and an attachment, never a
  path. The file path is made by Clippy from a random name inside its own folder.
- **GET only.** Other methods on `/files/...` answer `405` with an empty body.
  `HEAD` isn't served. A `Range` header is ignored: the whole file is always sent,
  so a partial request can't use the link up.
- **Unknown, expired or used links answer `404`** with an empty body and no detail.
  The same goes for a token that isn't 43 characters of the allowed letters, and for
  a link whose file has gone missing. `401` (no or wrong bearer token, or an
  `Origin` header) has an empty body too.
- **Safe download headers.** `Content-Type: application/octet-stream`,
  `Content-Disposition: attachment` with a cleaned file name, `X-Content-Type-Options:
  nosniff` and `Cache-Control: no-store`.
- **Logs.** The link token is in the URL, so the web server's own request logging is
  held to Warning level, and any `/files/<token>` that still reaches a log is masked
  as `/files/[redacted]`. Clippy's own lines name only outcomes and sizes, never the
  token, the path or the file name.
- **Activity.** A download shows as `attachment download` or `attachment download
  failed`, with no file name. See [Activity](activity.md#tool-calls).

Clippy still never sends anything, never deletes or moves mail and never touches
Outlook items for this: it only saves a copy of the attachment, like
`get_attachment`, and deletes its own copy.

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

## Data flow

Where your data goes, in one table.

| Data | Stays on your PC | Leaves your PC |
|---|---|---|
| Mail bodies, subjects, addresses | In the local index and in Outlook | Only for [AI drafts](ai-drafts.md), if you turned them on (see below). Never otherwise |
| Attachment contents | Not indexed; saved only when a tool call exports them to an allowed folder, or as a short-lived [download link](#attachment-download-links) copy in a private folder | Never over the network. A download link is fetched over `127.0.0.1` by your own shell |
| Attachment names | In the index | With the email, only for AI drafts |
| Related earlier emails | In the index | Up to 5 earlier emails from the same mailbox (own text only), only for AI drafts while **Include related earlier emails** is on. Not stored or logged |
| Your facts file | `%USERPROFILE%\.claude\skills\my-email-context\SKILL.md` (you write it; Clippy never changes it after creating the template) | Its text, without comments, goes to Claude with every AI draft request. Don't put secrets in it |
| Calendar and free/busy | Read from Outlook when you ask for times | Never. Meeting times in drafts are computed by Clippy after Claude has answered |
| Bearer token | DPAPI-encrypted file | Only to the MCP client you gave it to, over `127.0.0.1` |
| Anthropic API key (optional) | DPAPI-encrypted file | Only to `api.anthropic.com`, as your own key |
| Claude credentials (Claude Code sign-in) | Never read by Clippy | Never |
| Update check | Nothing | A GitHub request with a `ClippysRevenge/<version>` user agent |
| Telemetry | None | None |

## Where data lives on disk

| What | Where |
|---|---|
| Settings | `%APPDATA%\ClippysRevenge\settings.json` |
| Token, API key | `%APPDATA%\ClippysRevenge\token.bin`, `anthropic-key.bin` (DPAPI) |
| Index (plain text, one file per mailbox) | `%LOCALAPPDATA%\ClippysRevenge\index.db`, `index-<address>.db` |
| Logs (7 days) | `%LOCALAPPDATA%\ClippysRevenge\logs` |
| Facts file | `%USERPROFILE%\.claude\skills\my-email-context\SKILL.md` (written by you; Clippy only creates the template once), plus `facts-state.json` in `%APPDATA%\ClippysRevenge\my-email-style\` |
| Writing-style skill | `%USERPROFILE%\.claude\skills\my-email-style\SKILL.md`, plus Clippy's copy and `state.json` in `%APPDATA%\ClippysRevenge\my-email-style\` |
| Temporary files for Claude Code | `%LOCALAPPDATA%\ClippysRevenge\ai\` (one system-prompt file per run, deleted afterwards; mail text is never written there) |
| Saved attachments | `%USERPROFILE%\Downloads\Clippy's Revenge` (default) |
| Download-link copies | `%LOCALAPPDATA%\ClippysRevenge\links\` (your user only; deleted after download, on expiry and at app start; normally empty) |
| Draft-invite tracking | `%APPDATA%\ClippysRevenge\draft-invites.json` (Outlook ids only) |

**To wipe it:** use **Rebuild index...** in the tray to delete and recreate only the
index. To remove everything, uninstall with `-Purge` (see
[Uninstall](install.md#uninstall)), which deletes settings, token, index and
logs (check afterwards that `%APPDATA%\ClippysRevenge` is gone, as it also holds the
saved API key). Then delete by hand what lives outside Clippy's folders: the style skill folder
`%USERPROFILE%\.claude\skills\my-email-style`, the facts folder `%USERPROFILE%\.claude\skills\my-email-context` and any saved attachments. Drafts that
Clippy saved are ordinary Outlook drafts; they stay in your mailbox until you delete
them.

## AI drafts data flow

[AI drafts](ai-drafts.md) are the one feature that sends mail text off your PC. They
are off by default and need your consent. While on, Clippy sends to Anthropic (through
your Claude Code or your API key):

- the email being answered, with its quoted history (cut at 20,000 characters)
- up to 8 earlier messages of the same thread from the local index, your own text only
- up to 5 related earlier emails from the same mailbox, your own text only, at most 4,000
  characters in all (see [Related earlier mail](ai-drafts.md#related-earlier-mail)); you can
  turn this off with **Include related earlier emails**
- the text of your [facts file](ai-drafts.md#facts-about-you), without comments, when you
  filled it in. Don't put passwords or other secrets in it
- attachment **file names**, never attachment contents
- your name and address, today's date, and your writing-style guide
- once a week, about 150 to 300 samples of your sent mail for the style guide, with
  addresses, links and phone numbers removed
- when Clippy re-checks an [outdated draft](ai-drafts.md#outdated-drafts), the text of
  its own earlier draft and the newer messages of the thread

**Never sent:** attachment contents or any file, other mail or folders from the index,
your calendar, mail from shared mailboxes you haven't turned on, your Claude
credentials, and your API key (except to Anthropic as the key itself).

The model gets **no tools**: it can't read files, search mail or run anything. Its
answer is validated and cleaned to plain formatting and saved as a reply draft, never
sent. Claude Code (or the API) and your Claude plan terms govern what happens to the
data after it leaves Clippy.

Clippy never reads Claude Code's stored credentials, never calls an API with them,
and never offers a claude.ai sign-in. It runs your own `claude` command, or uses an
API key you typed in (stored DPAPI-encrypted). Logs and the Activity window don't
contain mail text, prompts, your facts, related mail, the style guide, the reason line or Claude's answers.

!!! warning "The facts file is visible to Claude Code"
    The facts file is also a user-level Claude Code skill, %USERPROFILE%\.claude\skills\my-email-context\SKILL.md,
    so your interactive Claude Code sessions (which have tools) can load it. Keep
    secrets out of it. Delete the folder if you don't want that. See
    [Facts about you](ai-drafts.md#facts-about-you).

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
tool names, counts and errors. The token, the saved API key and any `sk-ant-…` or `Bearer …` text are masked, as is the token in an attachment `/files/<token>` link, and mail bodies and search
text are not logged. Activity events show subjects and counts only. The SQL of a `query_index` call and the text of a tool's error message (which can quote a search or a SQL error back to Claude) are not logged either, only the kind of error. Tool calls appear as the tool name, with counts and timings.

## Unsigned executable

The release exe is not code-signed yet. The installer and the automatic updater check
its SHA-256 against `SHA256SUMS.txt` from the same release before installing. That
proves integrity, not the publisher. See [Update trust model](#update-trust-model).
