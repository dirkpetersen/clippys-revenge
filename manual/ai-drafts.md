# AI drafts

Clippy's Revenge can write reply drafts for you, in your own writing style, using
Claude. It is **off by default**. This page explains what it does, exactly what is
sent to Claude, and how to set it up and fix it.

## What it does and doesn't do

- **Drafts only.** A reply is saved in your Drafts folder. Clippy never sends it.
  You read it, edit it and send it from Outlook.
- **Never deletes or moves mail.** The only move is the new reply draft going into
  Drafts, the same as for [`reply_draft`](tools.md#reply_draft).
- **Reply subjects start with `Re:: `** (double colon), like every reply draft.
  The model can't change the subject or the recipients. It can only choose reply or
  reply all.
- **Only new mail.** It looks at mail that arrives in your Inbox **after** you turn
  it on, and never at mail older than the age limit (2 days by default). There is no
  backlog pass.
- **Your mailbox only**, unless you turn on a shared mailbox one by one.
- **Not every email gets a draft.** Newsletters, notifications, automatic mail and
  mail that needs no answer are skipped. See [Rules](#rules).
- **Your signature** is added, as for `reply_draft`.
- **Check every draft.** The model can make mistakes. Where a reply needs a fact,
  such as a date or a number, it is told to leave a placeholder like `[[date]]`
  instead of inventing one.

When a pass creates drafts, the tray shows a balloon: "It looks like you got mail. I
drafted 2 replies — they're in your Drafts folder." Click it to open Drafts.

## Set up

You need one of two ways to reach Claude. Pick one in Settings, **AI drafts**.

### Claude Code on this PC (default)

Uses your own Claude Code, signed in with your Claude plan.

1. [Install Claude Code](https://claude.com/claude-code).
2. Open a terminal, run `claude` and sign in. **You sign in yourself.** Clippy never
   signs in for you, never offers a claude.ai login, and never reads your Claude
   credentials. It only asks `claude auth status --json` whether you are signed in.
3. In the tray, right-click, **AI drafts**, **AI drafts settings...**. Under **Use**,
   keep **Claude Code on this PC (your Claude subscription)** and press **Check**.
   The status line should show a check mark, for example
   `✓ Claude Code 2.1.323 · signed in (<sign-in method>)`.

Clippy runs `claude` headless, without a shell, with no tools for the model, and
never with `--bare`. It finds `claude.exe` on your `PATH`, in
`%USERPROFILE%\.local\bin`, or an npm install of Claude Code. Script shims
(`.cmd`, `.bat`, `.ps1`) are never run. If yours is somewhere else, set **Claude Code
path** to the full path of `claude.exe`.

### Anthropic API key

Choose **Anthropic API key**, paste the key and press **Save key**. The key is stored
encrypted with Windows DPAPI for your Windows user, is never shown again, and is
never written to settings or logs. **Remove key** deletes it. Press **Check**: it
reads the model from the API, which uses no tokens.

### Model, effort and cost

The default model is **Opus 5.5 at medium effort**. You can change **Model** and
**Effort** in the tab. Usage and cost come from your Claude plan or your API
account, not from Clippy. Each email that Clippy evaluates is one Claude call,
whether or not it ends up as a draft. **Daily limit** (default 30) caps the calls
per day across all mailboxes. The tab shows `Today: 3 of 30 Claude calls, 1 draft`.

### Turn it on

Check **Draft replies to new email automatically** in the tab, or use the tray menu
item **Draft replies automatically**. The first time, Clippy asks for your
[consent](#consent-and-what-is-sent). If Claude isn't ready, the tray opens the AI
drafts tab so you can fix it first.

## Consent and what is sent

The first time you turn it on (and before the first manual draft), a dialog titled
**Turn on AI drafts?** says:

> Clippy will send new emails from your Inbox, earlier messages in the same thread,
> and samples of your sent mail to Claude (Anthropic) to write reply drafts in your
> style. Drafts are only saved, never sent. With Claude Code, this uses your Claude
> plan's usage. Shared mailboxes are included only if you turn them on one by one.

Buttons: **Turn on** and **Cancel** (the default).

While the feature is on, this leaves your PC for Anthropic, through Claude Code or
the API:

| What | Details |
|---|---|
| The email being answered | Sender, recipients, date, subject and the **full body including quoted history**, cut at 20,000 characters |
| Thread excerpts | Up to 8 earlier messages of the same conversation from your local index, your own text only (quoted history and signatures cut), each cut at 4,000 characters |
| Attachment **names** | For the email and the thread messages. Names only |
| Your name and address, today's date | So the draft is addressed and dated correctly |
| Your writing style guide | The skill described [below](#your-writing-style), which contains short examples of your sent mail with addresses, links and phone numbers removed |
| About 150 to 300 samples of your sent mail | Sent once a week, with addresses, links and phone numbers removed, to write the style guide |

**Never sent:**

- Attachment contents or any file.
- Anything else from your index: other mail, your calendar, other folders.
- Mail from shared mailboxes you haven't turned on.
- Your Claude credentials. Clippy never reads them.

**The model gets no tools.** It can't read files, run commands, search your mail or
call anything. Its answer is limited to: needs a reply or not, how sure it is, reply
or reply all, and a short HTML body. A sanitizer then removes everything except plain
formatting (paragraphs, line breaks, lists, bold, italic, underline, quotes), so
links, images, styles and scripts never reach the draft. A prompt-injected email can
at worst produce a bad draft, which you see before sending.

What Claude Code and Anthropic do with the data is governed by your Claude plan or
API terms. See also [Security](security.md#ai-drafts-data-flow).

## Rules

Before any email goes to Claude, cheap rules skip mail that needs no reply. The
first rule that matches wins. No Claude call is made for skipped mail.

| Skipped when | Notes |
|---|---|
| You sent it | The sender is your own address |
| No-reply sender | `noreply`, `no-reply`, `donotreply`, `mailer-daemon`, `postmaster`, `bounce` and similar |
| Automatic subject | Starts with `Automatic reply:`, `Autoreply:`, `Out of Office`, `Undeliverable:`, `Delivery Status Notification` |
| **Never draft for** | Sender matches your exclusion list |
| **Only draft for** | The list isn't empty and the sender isn't on it |
| **Only when I'm in To or CC** | On by default. Your address isn't in To or CC. Mail sent to an alias address is skipped |
| Already answered | You or Clippy already replied, or a draft reply already exists |
| Not normal mail | Meeting requests, receipts, encrypted mail and similar |
| Newsletters and lists | `List-Id`, `List-Unsubscribe` or bulk `Precedence` headers |
| Automatic mail | An `Auto-Submitted` header other than `no` |

**Never draft for** and **Only draft for** take one entry per line: an address
(`alex@example.org`) or a domain (`example.org` or `@example.org`, which also covers
subdomains). Each list holds up to 200 entries.

Then the model decides whether a reply is needed. It skips newsletters, FYIs, mail
where you are only on CC and nothing is asked of you, and thank-you notes that close
a conversation. A draft is saved only when its confidence reaches **Minimum
confidence**.

| Setting | Default |
|---|---|
| Model | Opus 5.5 (`claude-opus-5-5`) |
| Effort | medium |
| Daily limit | 30 Claude calls |
| Minimum confidence | 0.6 |
| Ignore mail older than | 2 days |
| Only when I'm in To or CC | on |
| Update weekly (writing style) | on |

### Limits and backoff

- At the **Daily limit**, Clippy stops and continues the next day. The Activity
  window shows `AI drafts: daily limit of 30 reached. I'll continue tomorrow.`
- When Claude's usage limit is reached, Clippy waits until the reset time if it
  knows it, otherwise an hour.
- After other failures it retries after 1, 2, 4, 8, 16, 32 and then 60 minutes, and
  gives up on a single email after 3 failed attempts.
- Nothing happens while indexing is paused or Outlook isn't running.

### Shared mailboxes

Under **Mailboxes** in the tab, **My mailbox** is always on while AI drafts are on.
Each [shared mailbox](indexing.md#shared-mailboxes) you index has its own checkbox,
off by default. Drafts for a shared mailbox use your own writing style.

### Draft one reply now

Select one email in Outlook and choose **Draft a reply to the selected email** from
the tray's **AI drafts** menu. This works even when automatic drafting is off, still
needs your consent and a ready Claude, and counts toward the daily limit. It skips
the rules and the confidence check, because you asked for it.

## Your writing style

Clippy learns your style from your own sent mail and keeps it in a Claude Code
**skill** file, `my-email-style`.

- It samples about 200 emails from the last 12 months of your Sent Items, a mix of
  internal and external, short and long, newest first, at most 3 per recipient.
- It uses only **your own text**. Quoted history and signatures are cut, and
  automatic replies and meeting responses are left out.
- **Mail Clippy drafted for you is excluded**, even after you edited and sent it.
  So are replies to mail that Clippy drafted an answer for, so the style doesn't
  learn from its own output.
- Email addresses, links and phone numbers are replaced by `<email>`, `<link>` and
  `<phone>` before anything is sent to Claude, and again in Claude's answer.
- It needs at least 20 suitable sent emails.
- Clippy writes the file's header. Claude writes only the body: greetings and
  sign-offs, tone, length, formatting habits, phrases you use and avoid, and a few
  short examples.

**Where the files are**

| File | Purpose |
|---|---|
| `%USERPROFILE%\.claude\skills\my-email-style\SKILL.md` | The skill. Your copy to read and edit |
| `%APPDATA%\ClippysRevenge\my-email-style\SKILL.md` | Clippy's own copy of what it last wrote |

The drafting prompt uses the file in `.claude\skills`, so your edits count.

**Your edits are never overwritten automatically.** If you changed the file (or you
wrote a `my-email-style` skill yourself before), Clippy leaves it alone. **Update
weekly** then does nothing, and the Activity window says `writing style not updated:
you edited it. Use Regenerate in Settings to replace it.` **Regenerate** asks `You
edited your writing style skill. Replace your edits with a new version?` with
**Replace** and **Keep mine** (the default).

**Refresh.** With **Update weekly** on, Clippy creates the skill before the first
drafting pass if there isn't one, and then refreshes it at most once every 7 days
when at least 20 new sent emails have appeared. Style generation doesn't count
toward the daily limit. In Settings you also have **View** (read-only), **Regenerate**
(runs now and takes a few minutes) and **Open in editor**. The status reads
`Generated 2026-11-13 from 200 sent emails`, `Edited by you` or `Not generated yet`.

!!! warning "Security note: this skill is also loaded by Claude Code"
    `%USERPROFILE%\.claude\skills\my-email-style\SKILL.md` is a *user-level Claude
    Code skill*. That means your **interactive** Claude Code sessions, the ones with
    tools, can load it. It is the one place where text derived from mail reaches a
    Claude session that has tools. The file is model output based on your sent mail,
    so treat it with care.

    What limits the risk: only your own text is sampled (quoted mail from other
    people is cut), addresses, links and phone numbers are removed from the samples
    and from the result, the prompt treats the samples as data and never as
    instructions, and Clippy writes the file header itself.

    You stay in control: **View** or **Open in editor** shows the file, you can edit
    it, and you can delete `%USERPROFILE%\.claude\skills\my-email-style` at any time.
    Clippy then falls back to its own copy in `%APPDATA%\ClippysRevenge` and, if you
    delete that too, to the plain style "write plainly, briefly and politely" until
    you press **Regenerate**. If you don't want the skill visible to your Claude Code
    sessions, delete the folder in `.claude\skills` after each update.

## Troubleshooting

The status line under **Use** and the Activity window tell you what is wrong. Fix it,
then press **Check**.

| Message | What to do |
|---|---|
| `✗ Claude Code wasn't found. Install it from https://claude.com/claude-code, sign in once in a terminal, then press Check.` | Install Claude Code, or set **Claude Code path** to `claude.exe`. A `.cmd` or `.ps1` shim isn't supported |
| `✗ Claude Code <version> is too old for AI drafts. Run "claude update", then press Check.` | Run `claude update` in a terminal |
| `✗ Claude Code is installed but not signed in. Open a terminal, run claude and sign in, then press Check.` | Run `claude` and sign in yourself. Clippy can't do it |
| `✗ Claude Code didn't answer (version)` and similar (`help`, `auth status`) | Press **Check** again. If it repeats, run `claude --version` in a terminal |
| `✓ Claude Code <version> · signed in (...)` plus `· update Claude Code for best results` | Works. Updating lets Clippy keep your own CLAUDE.md, hooks and plugins out of drafting calls |
| `✗ No API key saved.` | Paste your key and press **Save key** |
| `✗ The API key was rejected (HTTP 401).` | The key is wrong or revoked. Save a new one |
| `✗ The model "<model>" isn't available with this key.` | Change **Model** |
| `✗ api.anthropic.com didn't answer.` | Check your network and try again |
| `AI drafts: Claude isn't signed in` (Activity, error state) | Sign in again in a terminal, then press **Check**. Drafting resumes |
| `AI drafts: Claude's usage limit was reached. I'll try again at 14:05.` | Wait. It retries by itself |
| `AI drafts: a Claude call failed (<kind>). I'll try again in 4 min.` | Usually temporary. If it keeps failing, press **Check** |
| `AI drafts: daily limit of 30 reached. I'll continue tomorrow.` | Raise **Daily limit** or wait |
| `AI drafts: a reply draft couldn't be saved in Outlook. I'll try again later.` | Check that Outlook is running and Drafts is available |
| `Select one email in Outlook first.` | Manual draft: select one email |
| `That email isn't in your mailbox or an enabled shared mailbox.` | Manual draft: turn the shared mailbox on first |
| `Claude isn't ready: ...` | Manual draft: fix the status shown, then try again |
| `I couldn't draft that reply: ...` | The reason follows, for example Claude's usage limit, or that Outlook isn't running |
| `AI drafts need the email index, which couldn't be opened. Check the tray icon for the reason.` | The index failed to open. See [Troubleshooting](troubleshooting.md) |
| `Clippy doesn't know your email address yet. Start Outlook, wait for the first sync, then try again.` | Needed to write the style. Start Outlook and let it sync |
| `Only 12 sent emails found. Clippy needs 20.` | Not enough sent mail to learn from yet |
| `There is no writing style skill yet. Press Regenerate to create one.` | Press **Regenerate** |

If an email gets no draft, the usual reasons are: it arrived before you turned AI
drafts on, it is older than the age limit, it matched a [rule](#rules), or Claude
decided no reply was needed or wasn't confident enough.

Logs and the Activity window never contain mail text, prompts, the style skill or
Claude's answers.
