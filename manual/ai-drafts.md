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
- **Meeting times** are added by Clippy from your calendar, never written by the model.
  See [Meeting times](#meeting-times).
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
| Your writing style guide | The skill described [below](#your-writing-style), which contains short examples of your sent mail with addresses, links and phone numbers removed. Its meeting-times section is left out |
| About 150 to 300 samples of your sent mail | Sent once a week, with addresses, links and phone numbers removed, to write the style guide |

**Never sent:**

- Attachment contents or any file.
- Anything else from your index: other mail, your calendar (also for meeting times: free/busy is read by Clippy after Claude has answered), other folders.
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
| Offer meeting times when an email asks for a meeting | on |
| Number of times to offer | 5 (1 to 10) |

### Limits and backoff

- At the **Daily limit**, Clippy stops and continues the next day. The Activity
  window shows `AI drafts: daily limit of 30 reached. I'll continue tomorrow.`
- When Claude's usage limit is reached, Clippy waits until the reset time if it
  knows it, otherwise an hour.
- After other failures it retries after 1, 2, 4, 8, 16, 32 and then 60 minutes, and
  gives up on a single email after 3 failed attempts.
- Nothing happens while indexing is paused or Outlook isn't running.

### Emails Clippy decides not to draft

An email is **decided once**. When Claude says no reply is needed, or isn't
confident enough, that email is not looked at again. Only emails whose Claude call
**failed** are retried, up to 3 attempts.

When Claude is not confident enough, the Activity window shows a line, with the time
the email was received and no subject, sender or text:

> AI drafts: no draft for the email received 2026-11-13 14:05: Claude's confidence
> 0.55 is below your minimum of 0.60. To draft it anyway, select it in Outlook and
> use "Draft a reply to the selected email" in the tray menu, or lower Minimum
> confidence in Settings → AI drafts.

To get a draft for that email, select it in Outlook and use
[**Draft a reply to the selected email**](#draft-one-reply-now), which skips the
confidence check. Lowering **Minimum confidence** only affects mail that arrives
afterwards; mail that was already decided is not reconsidered. The summary line
after a pass counts these emails separately, for example
`AI drafts: 1 drafted, 2 low confidence (1.8 s)`, and the log shows the highest
confidence seen against your minimum.

### Shared mailboxes
Under **Mailboxes** in the tab, **My mailbox** is always on while AI drafts are on.
Each [shared mailbox](indexing.md#shared-mailboxes) you index has its own checkbox,
off by default. Drafts for a shared mailbox use your own writing style.

### Draft one reply now

Select one email in Outlook and choose **Draft a reply to the selected email** from
the tray's **AI drafts** menu. This works even when automatic drafting is off, still
needs your consent and a ready Claude, and counts toward the daily limit. It skips
the rules and the confidence check, because you asked for it.

## Meeting times

When an email asks for or suggests a meeting or a call, the draft can offer concrete
times from your calendar. **Clippy computes them, not the model.** The model only
says that a meeting was asked for (and, if the email says so, how long it should be
and in which time frame), and writes a short lead-in sentence in your style followed
by a `[[meeting_times]]` placeholder. After the model has answered, Clippy reads
Outlook free/busy and replaces the placeholder with the options. The model never
sees or types a time, and your calendar is never sent to Claude.

Example, for a sender in Europe:

```
1. Tue 11/18  10:00–11:00 PT  (19:00–20:00 CET)
2. Wed 11/19  14:00–15:00 PT  (23:00–00:00 CET)

Outside European office hours:
3. Thu 11/20  08:00–09:00 PT  (17:00–18:00 CET)
```

Each option is one line, so you can delete the ones that don't suit before you send.
The draft looks like this in Outlook: one option per line, with the columns kept
aligned.

**Who is checked.** You, the sender, and, when the reply is a reply to all, everyone
in To and CC (at most 10 other people, the sender first). People whose free/busy
can't be read, for example people outside your organization, are left out of the
check, so confirm with them yourself.

**When and how long.** From the start of the next day (or the earliest date the
email names, if later) for 10 days, or until the latest date the email names, and
never more than 60 days ahead. The length comes from the email and is 60 minutes
when it doesn't say. Working hours, business days and whether tentative counts as
busy come from [Settings, Scheduling](tray.md#scheduling).

**Ranking.** The options are ranked by your calendar: times right before or after
one of your meetings first (within 15 minutes, on the same day), then times next to
a tentative meeting, then the rest, each group in date order. Free/busy can't tell a
meeting from other busy time such as a focus block, so "next to a meeting" means
"next to busy time". Within each free block, options start in 30-minute steps (plus one that ends exactly
when the block ends), and they never overlap each other. At most **Number of times to offer** options are shown, both lists together.

**Senders in Europe.** For a sender who looks European, each option also shows the
time in Central European time, always written **CET**, also in summer, when the
actual offset is CEST. Options between 08:00 and 18:00 CET come first. If there
aren't enough, the others follow in a second list headed
`Outside European office hours:`. When the email is in another language, the model
translates that heading. If you are in the CET zone yourself, or Windows has no
Central European time zone, the CET part and the second list are left out.

Whether a sender is in Europe is guessed from two things: the time-zone offset in
the email's `Date` header, and the country at the end of the sender's address (for
example `.de`, `.fr`, `.eu`, `.co.uk`).

| `Date` offset | European country address (`.de`, `.fr`, ...) | Other country address (`.za`, `.jp`, ...) | `.com`, `.org`, `.edu`, ... |
|---|---|---|---|
| `+0100`, `+0200` | Europe | not Europe | Europe |
| `+0000`, `+0300` | Europe | not Europe | not Europe |
| anything else | not Europe | not Europe | not Europe |
| none | Europe | not Europe | not Europe |

This is a guess and can be wrong:

- Mail from inside your own Exchange organization has no such header, so only the
  address is used.
- A European sender with a `.com` address in winter UK or Portugal time (`+0000`) or
  eastern summer time (`+0300`) isn't detected.
- `by`, `ru`, `tr`, `ge`, `am`, `az` and `kz` addresses aren't counted as European.
- A sender's mail program can write any offset.

If the guess is wrong, delete the CET part and the second list by hand, or ask Claude
Code for times yourself (see [the skill](#the-meeting-times-section-of-the-skill)).

**When Clippy can't offer times.** The draft is still created, with a highlighted
placeholder for you to fill in:

| Placeholder | Meaning |
|---|---|
| `[[add meeting times: no free time found between 2026-11-15 and 2026-11-25]]` | Nobody is free in that range. Check by hand |
| `[[add meeting times: Clippy couldn't read your calendar]]` | Your free/busy couldn't be read, for example Outlook was busy |

When the sender proposes a specific time, the model doesn't accept or decline it. It
leaves a placeholder such as `[[check whether Tue 3 pm works]]` and you still get
the options. Like all placeholders, check these before sending.

**Settings** (Settings, **AI drafts**):

- **Offer meeting times when an email asks for a meeting**: on by default. When off,
  drafts contain no meeting times and your calendar is not read.
- **Number of times to offer**: 1 to 10, default 5.

This works in automatic passes and in **Draft a reply to the selected email**.
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

### The meeting-times section of the skill

The end of `SKILL.md` always has a section, **Offering meeting times**, written by
Clippy between the markers `<!-- clippy:meeting-times v1 -->` and
`<!-- /clippy:meeting-times -->`. It teaches your **interactive** Claude Code
sessions the same habit: to get times from [`find_free_times`](tools.md#find_free_times)
instead of making them up, to paste its `offer_text` unchanged, one option per line,
and to set `offer_cet` when writing to someone in Europe. It also says to leave the
options in the ranked order, to say that you will suggest times if none are found,
and not to book anything unless you ask. A short paragraph on how **you** usually
introduce meeting options is taken from your own sent mail.

- A **new or regenerated** skill always gets the section.
- A skill written **before** this feature gets the section added automatically, once,
  when Clippy starts, with no Claude call, as long as **you haven't edited the file**.
  The Activity window then shows `writing style: added how to offer meeting times`.
- If you **edited** the skill, or wrote a `my-email-style` skill yourself, Clippy
  doesn't touch it. **Regenerate** replaces it, including the new section.
- Changing anything inside the section counts as editing the file, like any other
  change.
- The AI drafting prompt leaves the section out. It is meant for Claude Code sessions
  with tools, not for drafting.
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
| `AI drafts: no draft for the email received ...: Claude's confidence 0.55 is below your minimum of 0.60.` | Draft it by hand from the tray, or lower **Minimum confidence**. See [Emails Clippy decides not to draft](#emails-clippy-decides-not-to-draft) |
| `[[add meeting times: ...]]` in a draft | Clippy couldn't offer times. See [Meeting times](#meeting-times) |
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
decided no reply was needed or wasn't confident enough (the Activity window says so\nin the second case).

Logs and the Activity window never contain mail text, prompts, the style skill or
Claude's answers.
