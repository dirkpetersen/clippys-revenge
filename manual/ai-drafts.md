# AI drafts

Clippy's Revenge can write reply drafts for you, in your own writing style, using
Claude. It is **off by default**. This page explains what it does, exactly what is
sent to Claude, and how to set it up and fix it.

## What it does and doesn't do

- **Drafts only.** A reply is saved in your Drafts folder. Clippy never sends it.
  You read it, edit it and send it from Outlook.
- **Never sends, never deletes mail.** Nothing is ever permanently deleted and
  Deleted Items is never emptied. Clippy moves only two kinds of item: the new reply
  draft going into Drafts (the same as for [`reply_draft`](tools.md#reply_draft)), and,
  since version 0.1.0, one of **its own unedited AI drafts** that a newer message in
  the thread has made obsolete, which goes to Deleted Items so you can still get it
  back. See [Outdated drafts](#outdated-drafts).
- **Reply subjects start with `Re:: `** (double colon), like every reply draft.
  The model can't change the subject or the recipients. It can only choose reply or
  reply all.
- **Only new mail.** It looks at mail that arrives in your Inbox **after** you turn
  it on, and never at mail older than the age limit (2 days by default). There is no
  backlog pass. The one thing that looks further back is the manual
  [rescan of the last 24 hours](#rescan-emails-from-the-last-24-hours).
- **Your mailbox only**, unless you turn on a shared mailbox one by one.
- **Not every email gets a draft.** Newsletters, notifications, automatic mail and
  mail that needs no answer are skipped. See [Rules](#rules).
- **Attachments are read.** Since version 0.1.1, Clippy also reads the text of the
  attachments of the email and its thread, and of OneDrive and SharePoint files that
  are already synced to your PC, and sends that text to Claude so the reply can use
  it. You can turn it off. See [Attachments and shared files](#attachments-and-shared-files).
- **It learns from your edits.** Since version 0.1.1, when you send a reply that began
  as one of Clippy's AI drafts, Clippy notes how you changed it and uses that to
  improve your writing style. You can turn it off. See
  [Learning from how you edit drafts](#learning-from-how-you-edit-drafts).
- **Your signature** is added, as for `reply_draft`.
- **Meeting times** are added by Clippy from your calendar, never written by the model.
  See [Meeting times](#meeting-times).
- **Check every draft.** The model can make mistakes. Where a reply needs a fact,
  such as a date or a number, it is told to leave a placeholder like `[[date]]`
  instead of inventing one.
- **Nothing is ever sent.** Clippy has no send function at all, and the model has no
  tools: its answer can only become a saved draft.

When a pass creates or updates drafts, the tray shows a balloon: "It looks like you got
mail. I drafted 2 replies — they're in your Drafts folder." It counts new and updated
drafts together. Click it to open Drafts.

## Set up

You need one of two ways to reach Claude. Pick one in Settings, **AI drafts**.

### Claude Code on this PC (default)

Uses your own Claude Code, signed in with your Claude plan.

1. [Install Claude Code](https://claude.com/claude-code).
2. Open a terminal, run `claude` and sign in. **You sign in yourself.** Clippy never
   signs in for you, never offers a claude.ai login, and never reads your Claude
   credentials. It only asks `claude auth status --json` whether you are signed in.
3. In the tray, right-click, **AI drafts**, **AI drafts settings…**. Under **Use**,
   keep **Claude Code on this PC (your Claude subscription)** and press **Check**.
   The status line should show a check mark, for example
   `✓ Claude Code 2.1.323 · signed in (<sign-in method>, <plan>)`, with what
   `claude auth status` reports in the brackets.

Clippy runs `claude` headless, without a shell, with no tools for the model, and
never with `--bare`. It looks for `claude.exe` on your `PATH`, then in
`%USERPROFILE%\.local\bin`, then for an npm install of Claude Code
(`%APPDATA%\npm\node_modules\@anthropic-ai\claude-code\cli.js`, run with the
`node.exe` on your `PATH`). Script shims (`.cmd`, `.bat`, `.ps1`) are never run. If
yours is somewhere else, set **Claude Code path (optional)** to the full path of
`claude.exe` or of Claude Code's `cli.js`.

### Anthropic API key

Choose **Anthropic API key**, paste the key and press **Save key**. The key is stored
encrypted with Windows DPAPI for your Windows user, is never shown again, and is
never written to settings or logs. **Remove key** deletes it. Press **Check**: it
reads the model list from the API, which uses no tokens. Clippy talks only to
`api.anthropic.com`, and never uses a key from environment variables.

Either way, Clippy never reads Claude Code's stored sign-in and never asks you to
sign in to claude.ai inside Clippy.

### Model, effort and cost

The default model is the alias **`opus`, the latest Opus**, at **medium** effort, for
drafting and for the writing style alike. Because it is an alias, drafts move to a
newer Opus without a Clippy update. You can change **Model** and **Effort** in the
tab. Next to the Model box, a small hint reads `latest Opus` (or `latest Sonnet`,
`latest Haiku`) while the box holds `opus`, `sonnet` or `haiku`. A specific model name
such as `claude-opus-5-5` is used exactly as typed. If you had the old default
`claude-opus-5-5` saved, Clippy changes it to `opus` once; any other model you chose
stays.

How the alias is resolved depends on the way you reach Claude:

- **Claude Code:** the alias is passed to `claude`, which picks its newest model of
  that family.
- **API key:** the API needs a concrete model name, so Clippy asks the API for the
  list of models available to your key and takes the newest active model of that
  family. It remembers the answer for 24 hours (in memory only). **Check** always
  looks it up fresh and shows, for example, `✓ API key works (opus → <model name>)`.
  If the lookup fails (not because of a rejected key), Clippy uses a built-in
  fallback model, logs a warning without any key or mail text, and tries the lookup
  again after 10 minutes. Saving or removing the key, and pressing **Check**, also
  start a new lookup.

Usage and cost come from your Claude plan or your API
account, not from Clippy. Each email that Clippy evaluates is one Claude call,
whether or not it ends up as a draft. **Daily limit** (default 30) caps the calls
per day across all mailboxes. The tab shows `Today: 3 of 30 Claude calls, 1 draft`.

Because attachment text makes requests longer, Clippy also keeps a **daily token
tally** so you can see the cost. See [Token tally](#token-tally).

### Turn it on

Check **Draft replies to new email automatically** in the tab, or use the tray menu
item **Draft replies automatically**. The first time, Clippy asks for your
[consent](#consent-and-what-is-sent). If Claude isn't ready, the tray opens the AI
drafts tab so you can fix it first.

## Consent and what is sent

The first time you turn it on (and before the first manual draft), a dialog titled
**Turn on AI drafts?** says:

> Clippy will send new emails from your Inbox, earlier messages in the same thread,
> the text of their attachments and of shared files synced to this PC, up to 5 related
> earlier emails, and samples of your sent mail and how you edited Clippy's drafts to
> Claude (Anthropic) to write reply drafts in your style. Drafts are only saved, never
> sent. With Claude Code, this uses your Claude plan's usage. Shared mailboxes are
> included only if you turn them on one by one.

Buttons: **Turn on** and **Cancel** (the default). If you consented to an older text,
you are not asked again. Instead you get one-time notices for what was added since, for
the edits see [Notice for existing users](#notice-for-existing-users).

While the feature is on, this leaves your PC for Anthropic, through Claude Code or
the API:

| What | Details |
|---|---|
| The email being answered | Sender, recipients, date, subject and the **full body including quoted history**, cut at 20,000 characters |
| Thread excerpts | Up to 8 earlier messages of the same conversation from your local index, your own text only (quoted history and signatures cut), each cut at 4,000 characters |
| Related earlier emails | Up to 5 earlier emails from the same mailbox that may relate to this one, own text only, at most 4,000 characters in all. Only while [Include related earlier emails](#related-earlier-mail) is on |
| Your earlier draft | Only when Clippy [re-checks an outdated draft](#outdated-drafts): the text of Clippy's own earlier draft (cut at 4,000 characters) and the newer messages of the thread. Nothing beyond what the consent text already names |
| Attachment **names** | For the email and the thread messages |
| Attachment **text** | Text extracted from the attachments of the email and its thread, and from OneDrive/SharePoint files linked in those emails that are already synced to your PC: at most 40,000 characters in all, 20,000 per file. Only while [Read attachments and synced shared files for drafts](#attachments-and-shared-files) is on. Sent for that one draft, never stored |
| Your name and address, today's date | So the draft is addressed and dated correctly |
| Your facts file | What you wrote in [Facts about you](#facts-about-you), without the comments. Only when you filled something in |
| Your writing style guide | The skill described [below](#your-writing-style), which contains short examples of your sent mail with addresses, links and phone numbers removed. Its meeting-times section is left out |
| Up to 200 samples of your sent mail | From the last 12 months, your own text only, each cut at 1,500 characters. Sent once a week, with addresses, links and phone numbers removed, to write the style guide |
| Up to 20 pairs of an AI draft and what you sent | Only with the writing-style update (weekly, or when you press **Regenerate**), and only when at least 3 pairs exist. Each side is your own text without the quoted email or signature, with addresses, links and phone numbers removed, at most 1,500 characters. Only while [Learn from how I edit Clippy's drafts](#learning-from-how-you-edit-drafts) is on |

**Never sent:**

- Any file itself. Only text that Clippy extracted from the attachments and synced
  files described [below](#attachments-and-shared-files) is sent, and images are
  never sent.
- Anything else from your index: other mail, your calendar (also for meeting times: free/busy is read by Clippy after Claude has answered), other folders.
- Mail from shared mailboxes you haven't turned on.
- Your Claude credentials. Clippy never reads them.

**The model gets no tools.** It can't read files, run commands, search your mail or
call anything. Its answer is limited to: needs a reply or not, how sure it is, reply
or reply all, a short HTML body and the one-sentence [reason line](#the-reason-line). A sanitizer then removes everything except plain
formatting (paragraphs, line breaks, lists, bold, italic, underline, quotes), so
links, images, styles and scripts never reach the draft. A prompt-injected email can
at worst produce a bad draft, which you see before sending.

What Claude Code and Anthropic do with the data is governed by your Claude plan or
API terms. See also [Security](security.md#ai-drafts-data-flow).

## Rules

These rules decide which new emails get a draft. (They don't decide whether an
existing draft is still current: any newer message in the thread triggers an
[outdated-draft check](#outdated-drafts).)

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
| Newsletters and lists | A `List-Id` or `List-Unsubscribe` header, or `Precedence` `bulk`, `list` or `junk` |
| Automatic mail | An `Auto-Submitted` header other than `no` |

**Never draft for** and **Only draft for** take one entry per line: an address
(`alex@example.org`) or a domain (`example.org` or `@example.org`, which also covers
subdomains). Each list holds up to 200 entries. You can add to **Never draft for**
straight from Outlook, see [Don't draft for this sender](#dont-draft-for-this-sender).

Then the model decides whether a reply is needed. It skips newsletters, FYIs, mail
where you are only on CC and nothing is asked of you, and thank-you notes that close
a conversation. A draft is saved only when its confidence reaches **Minimum
confidence**.

| Setting | Default |
|---|---|
| Model | `opus` (the latest Opus) |
| Effort | medium |
| Daily limit | 30 Claude calls |
| Minimum confidence | 0.6 |
| Ignore mail older than | 2 days |
| Only when I'm in To or CC | on |
| Update weekly (writing style) | on |
| Update or remove my AI drafts when a thread moves on | on |
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

### Don't draft for this sender

Since version 0.1.1 you can stop drafts for a sender without opening Settings. Select
an email from that sender in Outlook, then use one of two items in the tray's **AI
drafts** submenu:

- **Don't draft for this sender** adds the sender's address to **Never draft for**,
  with no dialog.
- **Don't draft for this sender's domain…** adds the domain after a confirmation,
  `Never draft replies to anyone at <domain>?`, with **Add** and **Cancel** (the
  default). A domain entry also covers its subdomains.

They need no consent, because nothing is sent. They work on the email you selected, in
your mailbox or an enabled shared mailbox, and are greyed out while Outlook isn't
running, while AI drafts services aren't available and while a rescan runs. The new
entry applies to the next email Clippy looks at. Balloons:

| Balloon | When |
|---|---|
| `I won't draft replies to <address> any more. You can undo this in Settings → AI drafts.` (`…to anyone at <domain>…` for a domain) | It was added |
| `I couldn't find that sender's email address.` | The email has no usable address |
| `That's your own address.` | The sender is you or your mailbox |
| `That's your own organization's domain. To stop drafting for all internal mail, add it in Settings → AI drafts.` | The domain item on your own domain, or a parent or subdomain of it |
| `<address or domain> is already on "Never draft for".` | An existing entry already covers it (a domain entry covers its addresses) |
| `"Never draft for" already has 200 entries. Remove some in Settings → AI drafts.` | The list is full |
| `Select one email in Outlook first.` or `That email isn't in your mailbox or an enabled shared mailbox.` | Nothing, several emails or an email from a mailbox that isn't on is selected |
| `Clippy doesn't know your email address yet. Start Outlook, wait for the first sync, then try again.` | Clippy needs your address to avoid blocking you. Nothing is added |
| `It looks like Outlook isn't running. I'll wait — or right-click me and pick Start Outlook.` | Outlook closed while you clicked |

For the domain item, the own-domain, "already" and "full" checks run **before** the
confirmation question, so you are not asked about something that would be refused. The
entry is checked again when it is saved. If the settings can't be saved, a balloon shows
the reason.

Activity shows `AI drafts: added an entry to "Never draft for" (<n> entries).` (`(1
entry)` for the first one) without the address.

**To undo:** open Settings, **AI drafts**, and delete the line (the address or the
domain) from **Never draft for**, then press OK. Clippy drafts for that sender again from
the next email on. There is no undo balloon or Activity action; the list in Settings is
the only place. Drafts that already exist for that sender are left as they are. If
Settings is open at the time, the addition is kept when you press OK, unless you also
edited the **Never draft for** box in that window.

### Draft one reply now

Select one email in Outlook and choose **Draft a reply to the selected email** from
the tray's **AI drafts** menu. This works even when automatic drafting is off, still
needs your consent and a ready Claude, and counts toward the daily limit. It skips
the rules and the confidence check, because you asked for it. When the draft is saved,
a balloon says `It looks like you want a reply. It's in your Drafts folder.` Drafts
made this way are never re-checked later: see [Outdated drafts](#outdated-drafts).

## Outdated drafts

Replies change. Say Clippy drafted a reply to a group email, and later someone in
the same thread writes to another person. Clippy rightly drafts nothing for that new
message, but the earlier draft may now be pointless. Since version 0.1.0 Clippy
re-checks its own drafts when a newer message arrives in their thread, and then does
one of three things:

- **Keep** the draft, because it still fits.
- **Replace** it with a new draft that takes the whole thread into account.
- **Remove** it, because you no longer need to reply. "Remove" means **moving it to
  Deleted Items**, where you can still find it. Nothing is permanently deleted and
  Deleted Items is never emptied.

If the model is unsure whether a draft is outdated, it keeps it. A replace or remove
also needs the model's confidence to reach **Minimum confidence**; otherwise the draft
stays as it is.

**Which drafts.** Only drafts that Clippy made through automatic drafting or a
[rescan](#rescan-emails-from-the-last-24-hours), and only up to 14 days after they
were created. Drafts from **Draft a reply to the selected email**, drafts made by
[`reply_draft`](tools.md#reply_draft), meeting notes, and AI drafts made before
version 0.1.0 are never touched.

**When.** At the start of each automatic pass, when a message newer than the one the
draft answers has reached your Inbox (or a folder under it) in the same thread. It
counts whoever the message is from and whoever it is addressed to, including mail
that isn't for you and mailing-list mail. Each re-check is one Claude call and counts
toward the **Daily limit**. At most 10 drafts per mailbox are re-checked in one pass,
and only the newest draft of a thread.

**When Clippy leaves a draft alone for good.** Before it moves or replaces anything,
Clippy checks the draft again. It acts only if all of these are true:

- the draft carries Clippy's own AI marker (`ClippyDraft = ai`);
- it is **unchanged** since Clippy saved it. Clippy compares a fingerprint of the
  subject, recipients, text, categories, importance, sensitivity, sending account and
  attachment names, so any edit you make, even one letter, counts;
- it is **unsent** and still in your **Drafts** folder;
- it is **not open** in a window (or as an inline reply in the reading pane);
- **you haven't replied** in the thread, and you haven't sent the draft itself.

If you edited the draft, Clippy never touches it again and adds no note to it. If the
draft is open, Clippy tries again at the next pass. If it was sent, moved or deleted,
or you answered the thread yourself, it is left alone. When a replace happens, the new
draft is **saved first**, and only then is the old one moved. If the old draft was
changed or opened in between, it stays in Drafts next to the new one, and the Activity
window says so.

**What you see in the Activity window** (the subject is shortened to 80 characters):

- `AI reply draft updated (the thread moved on): <subject>. The old draft is in Deleted Items.`
- `AI reply draft updated (the thread moved on): <subject>. The old draft is still in Drafts because it was changed or is open.`
- `Outdated AI reply draft moved to Deleted Items (no reply needed any more): <subject>`
- `AI drafts: an outdated draft couldn't be moved to Deleted Items. It's still in Drafts.`

The summary line after a pass adds `updated`, `moved to Deleted Items` and `kept`
counts, and the tray balloon counts new and updated drafts together.

**Turning it off.** Clear **Update or remove my AI drafts when a thread moves on** in
Settings, **AI drafts**. Clippy then never re-checks, replaces or moves a draft.

### Worked example

Dana Example sends a group email to you, Sam Sample and Lee Example, at
`dana@example.org`: "Who is coming skiing on Saturday? Please say yes or no." On
Thursday, Clippy's automatic pass drafts your reply: "Yes, count me in." You don't
open it. On Friday, Lee replies to everyone: "Sam, can you bring the roof rack?" That
message is for Sam, so Clippy drafts nothing for it. But the next pass notices that a
newer message arrived in the thread where Clippy has an unsent draft of its own. It
checks that the draft is unedited, still in Drafts, closed, and that you haven't
replied. Then it asks Claude, which sees the draft and the whole thread. Claude
answers that nothing is asked of you any more, and with enough confidence Clippy
moves the draft to Deleted Items. The Activity window shows: `Outdated AI reply draft
moved to Deleted Items (no reply needed any more): Re:: Skiing on Saturday`. Had Lee
instead asked you a new question, Claude would answer "replace", and Clippy would save
a fresh draft first and then move the old one.

## Rescan emails from the last 24 hours

In the tray, **AI drafts**, **Rescan emails from the last 24 hours…** runs the
drafting workflow once over the last day of mail, so you can watch it work. Use it to
catch up after Claude was unavailable, or after you changed the rules.

- It needs your consent, a ready Claude and a running Outlook, like **Draft a reply
  to the selected email**. It works while **Draft replies automatically** is off.
- It covers your own mailbox and the shared mailboxes you turned on. It takes Inbox
  mail from the last 24 hours, **oldest first**, ignoring the "newer than I turned it
  on" and age settings and the rule that each email is decided only once. The other
  rules still apply, and an email that already has a Clippy draft is skipped.
- Before it starts, a dialog titled **Rescan the last 24 hours?** shows how many
  emails will be looked at and how many Claude calls are left today, for example:
  `Clippy will go through 12 emails from the last 24 hours, oldest first, and draft a
  reply where one is needed. Each email is one Claude call: up to 12 calls, and 25 of
  today's 30 are left. The rescan stops at the daily limit. Outdated Clippy drafts in
  those threads may be replaced or moved to Deleted Items. Nothing is sent.` (The
  sentence about outdated drafts appears only while that setting is on.) Buttons:
  **Rescan** and **Cancel** (the default). With no emails to look at, or no calls
  left today, you get a short balloon instead.
- If a thread already has an open draft of Clippy's, and a later email in it is
  being rescanned, the rescan [re-checks that draft](#outdated-drafts) instead of
  drafting again.
- While it runs, the tray item reads **Cancel rescan (3/12)**. Cancel stops before
  the next email and cancels a call in progress. An email whose answer has already
  arrived is finished first, so nothing is left half done. **Draft a reply to the
  selected email** is disabled meanwhile, and no automatic pass runs alongside.
- Every call counts toward the **Daily limit**, and the rescan stops when it is
  reached.
- At the end, a summary appears in Activity and as a balloon (click it to open
  Drafts), for example: `Rescan: 12 emails, 3 drafts, 1 outdated draft moved to
  Deleted Items, 8 no reply needed (94 s)`.
- It can stop early, with `Rescan stopped (<reason>) after 5 of 12 emails: ...`. The
  reasons are: `cancelled`, `daily limit of 30 reached`, `Claude's usage limit was
  reached`, `Claude isn't signed in`, `Outlook isn't running` and `3 Claude calls
  failed in a row`.

Drafts made by a rescan are tracked like automatic ones, so a later newer message can
make them obsolete too.

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
busy come from [Settings, Scheduling](settings.md#scheduling).

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

## Facts about you

Clippy can give Claude a short file of facts about you, so that a draft uses your real
Zoom link, office hours or standard answer instead of a placeholder like `[[Zoom link]]`.
The same file can also say **how you want replies written**.

### Where the file is

```
%USERPROFILE%\.claude\skills\my-email-context\SKILL.md
```

It is a second Claude Code **skill**, next to your writing-style skill. Because of
that, your interactive Claude Code sessions read it too when they write email for you.
Clippy keeps no other copy: the file is the only source, and it is read fresh for every
draft, so a change applies to the next draft.

### How the file is created

Clippy creates the template **once**, and never overwrites a file that exists:

- The first time you have given [consent](#consent-and-what-is-sent) and Clippy knows
  your name from Outlook. If Outlook isn't running yet, Clippy waits, so the template
  never says "the user" just because Outlook was closed.
- When you choose **Edit my facts…** in the tray and the file is missing. This needs no
  consent, because nothing is sent. If your name isn't known yet, the template says
  "the user".

Clippy remembers that it created the template (a small file `facts-state.json` in
`%APPDATA%\ClippysRevenge\my-email-style`). If you delete the file yourself, Clippy
does not bring it back on its own. **Edit my facts…** creates it again.

### The template

```
---
name: my-email-context
description: "Facts about Ann Example for writing their email: role and team, meeting links, office hours, standard answers, who people are, and how they want replies written. Use when drafting or replying to email as Ann Example."
---

<!-- Facts about you, for Clippy's Revenge and Claude Code. Clippy adds what you write here to every AI draft request it sends to Claude, and Claude Code reads it when it writes email for you. Write short facts under the headings, one per line. Text between these comment markers is ignored, so you can leave the examples. Clippy never changes this file once it exists. Don't put passwords or other secrets here. -->

## Me

<!-- Your role, your team and what you work on. -->
<!-- e.g. I lead the research computing team; we run the HPC cluster and research storage. -->

## Links

<!-- Links you often send. -->
<!-- e.g. My Zoom room: https://zoom.us/my/example -->

## Office hours

<!-- When and how people can reach you. -->
<!-- e.g. Office hours Tuesdays 2-4 pm, on Zoom or in my office. -->

## Standard answers

<!-- Answers you give again and again. -->
<!-- e.g. New accounts: please fill in the request form at https://example.org/accounts -->

## People

<!-- Who people are and how you know them, one per line. -->
<!-- e.g. Ricky, Dave: ski group -->

## How I want replies written

<!-- Preferences for your drafts; these win over the writing-style skill. -->
<!-- e.g. Sign off with just my first name. Keep replies to my team short. -->
```

The name in the first lines is your Outlook display name (here the made-up Ann
Example).

### What to write

Write short facts under the headings, one per line. Text between `<!--` and `-->` is
ignored, so you can leave the examples in place or replace them.

- **Facts:** your role and team, links you send often, office hours, answers you give
  again and again, and who people are (`Ricky, Dave: ski group`).
- **Reply preferences**, under **How I want replies written**: your sign-off, how long
  replies should be, the tone for certain people. These **win over your
  [writing-style skill](#your-writing-style)** where the two differ, because you wrote
  them on purpose.

Preferences never win over the fixed rules. Whatever the file says, drafts are only
saved and never sent, mail text is data and never an instruction, and a fact that
neither the email nor the file gives still becomes a `[[…]]` placeholder. If the file
asks for something those rules don't allow, Claude ignores that part.

Don't put passwords or other secrets in the file: everything in it goes to Claude.

### What Claude gets

Only text you wrote. Clippy removes the header at the top and every `<!-- … -->`
comment, then any heading with nothing under it, so an untouched template adds nothing
at all. A file larger than **16 KiB**, a file that can't be read and a file that isn't
valid UTF-8 are not used: drafts then go without facts.

### Edit my facts…

In the tray, **AI drafts**, **Edit my facts…** opens the file in Notepad, and creates
the template first when the file is missing. If Notepad can't be started, a balloon says
`Couldn't open the file: <reason>`. Save the file in Notepad and you are done.

### Status in Settings

Settings, **AI drafts** shows a line under the check boxes:

| Line | Meaning |
|---|---|
| `Facts about you: filled in` | The file has text of yours. It goes with every draft |
| `Facts about you: template only` | The file exists but holds only the untouched template, so nothing is added |
| `Facts about you: not found` | The file is missing, too large, unreadable or not UTF-8. Use **Edit my facts…** |

### Activity messages

Clippy tells you when the file goes away or comes back, once per change and never with
its content: `Facts about you: my-email-context SKILL.md not found or unreadable — drafts go without it. Use "Edit my facts…" in the AI drafts menu.`
and `Facts about you: using my-email-context SKILL.md again.` See
[Activity](activity.md#facts-about-you).

### Worked example

Ann puts these lines in her file:

```
## Links
My Zoom room: https://zoom.example.org/my/ann

## People
Ricky, Dave: ski group

## How I want replies written
Sign off with just my first name.
```

Ricky writes: "Can we do a quick call about the Saturday bus?" The draft offers her Zoom
link instead of `[[Zoom link]]`, knows Ricky from the ski group, and signs off `Ann`.
A question the file doesn't answer, such as the bus departure time, still gets a
placeholder such as `[[bus time]]`.

## Related earlier mail

For better replies, Clippy can add a few earlier emails that may relate to the one it is
answering. The setting is **Include related earlier emails** in Settings, **AI drafts**,
**on by default**. It uses only your local index, so it adds no Claude call and no
Outlook read.

### What is picked

For the email being answered, Clippy looks in **the same mailbox** (never another one)
at mail from the **last 12 months**, in two ways:

- **Topic:** emails whose subject or body match the main words of this email's subject.
  Short and common words are ignored. If the subject has too few words, frequent words
  from the start of the text fill in.
- **People:** emails from the same sender, and your own **Sent Items** to that sender
  (To or CC). This is skipped when the sender is you.

It scores them (a topic match counts most, then a people match, then newer is better),
keeps **at most 5**, and shows them oldest first. If a Sent Items email to that person
exists, it is guaranteed a place, so Claude can see how you answered before.

Left out: the email itself and everything in its thread, mail in **Drafts** and
**Deleted Items** (also when Outlook shows them under another name, see
[Drafts and Deleted Items in other languages](#drafts-and-deleted-items-in-other-languages)),
copies of the same message in other folders, and anything newer than the email. Of
several emails in one thread only the best one is kept, and duplicates are dropped.

### What goes to Claude

Only the **own text** of each email (quoted history and signatures cut, attachment names
left out), with sender, recipients, date, folder and subject. Each email is cut to 800
characters, and the whole block holds at most **4,000 characters** of message text.
Claude is told that these emails are data, may be outdated, and are to be used only for
facts and tone that clearly apply. They are never stored or logged. If the lookup
fails, the draft is made without them.

### The one-time notice

If you gave consent before this feature existed (v0.1.0 or earlier), Clippy tells you
once, with a balloon and an Activity entry:
`AI drafts now also send up to 5 related earlier emails from your mailbox to Claude, to write better replies. You can turn this off in Settings → AI drafts.`
New consents already include it in the consent text. Untick **Include related earlier
emails** to send none.

## Attachments and shared files

A reply often depends on a file: the budget in the spreadsheet, the contract PDF, the
plan someone linked. Since version 0.1.1, Clippy reads the **text** of those files when
it builds a draft request and sends it to Claude with the email. The model can then
refer to a file by name ("the attached budget.xlsx", "the shared Plan.docx") and use
what it says. This applies to every kind of draft: automatic, **Draft a reply to the
selected email**, [outdated-draft](#outdated-drafts) re-checks and
[rescans](#rescan-emails-from-the-last-24-hours). It adds **no** extra Claude call.

It is a setting, **on by default**: **Read attachments and synced shared files for
drafts** in Settings, **AI drafts**. See [Turning it off](#turning-it-off-and-the-one-time-notice).

### What is read

For the email being answered, and for the earlier messages of its thread:

- **Attachments.** Every attachment that is not an inline picture (such as a signature
  logo). For the email itself, all of them. For earlier messages, Clippy looks at the
  newest 50 messages of the thread in your mailbox's index, at or before the email.
- **Attached emails** (an Outlook message attached to another message). Their subject,
  sender, date, attachment names and body (at most 20,000 characters) are read. Their
  own attachments are not. An attached email without a file name is named
  `<subject>.msg`.
- **Shared files.** Links in the text each person wrote to OneDrive or SharePoint, and Outlook
  "cloud attachments" (the share links Outlook adds when you attach a OneDrive file).
  At most 10 links per message. See [Shared files](#shared-files-onedrive-and-sharepoint).
- Skipped without reading: **images** (`png`, `jpg` and so on, only the name is
  listed), and any file type in the table below that Clippy can't read.

Earlier messages in the **Deleted Items** folder are never used: you threw them away.
Messages in **Drafts** are used, because a draft you are writing can carry the file
being discussed. Clippy's own earlier draft is not used when it re-checks that draft.
Earlier files are read from Outlook again for each draft, so a later reply in the same
thread that mentions an earlier attachment still sees it. If the earlier message is no
longer in your mailbox, or the attachment is no longer on it, that item is skipped.

### Formats

The format is chosen by the file extension and confirmed by the first bytes of the
file. A file that doesn't match is treated as damaged. Nothing is opened in another
program, executed or rendered. Macros, embedded objects, linked content and fields are
never read.

| Format | Extensions | What is read |
|---|---|---|
| PDF | `pdf` | Text of the first **30 pages**. No scans: a PDF without text is reported as maybe a scan (no OCR). A PDF that needs a password is skipped |
| Word | `docx`, `docm`, `dotx` | Paragraphs and tables (cells separated by tabs). Not headers, footnotes or comments |
| Excel | `xlsx`, `xlsm`, `xltx` | The first **5 sheets**, up to **200 rows** and **30 columns** each, as tab-separated lines under the sheet name. Cells are cut at 200 characters. Dates show as `yyyy-MM-dd` (`yyyy-MM-dd HH:mm` when the cell has a time). Formulas show their last calculated value |
| PowerPoint | `pptx`, `pptm`, `ppsx` | The first **60 slides**: the text of each slide, then its speaker notes |
| Text | `txt`, `text`, `csv`, `tsv`, `md`, `markdown`, `log`, `json`, `xml`, `ics`, `vcf` | The first 1 MiB. A file with binary content is reported as damaged |
| Web page | `html`, `htm` | The visible text of the first 1 MiB. Scripts and styles are dropped |
| Saved email | `eml` | Subject, sender, date and the text part |
| Outlook message | `msg` and attached emails | Read through Outlook itself, as described above |
| Not read | `doc`, `dot`, `xls`, `xlt`, `xlsb`, `ppt`, `pps`, `rtf`, `odt`, `ods`, `odp`, `pages`, `numbers`, `key`, `zip`, `7z`, `rar` | Skipped, with a yellow note. Save as `docx`, `xlsx` or `pdf` to have it read |
| Anything else | | Skipped, no note |

An encrypted Word, Excel or PowerPoint file is skipped as password-protected.

### Limits

These limits are fixed. There is no setting for them.

| Limit | Value |
|---|---|
| File size | 25 MB per file. A larger file is skipped without being read |
| Text per file | 20,000 characters. A longer file is cut and marked as only partly read |
| Text per draft | 40,000 characters for all files together |
| Files per draft | 20 files that need reading. Images and files Clippy can't read don't use a slot |
| Time per file | 10 seconds. A file that takes longer is skipped |
| Time per draft | 20 seconds for all files together. The files left are skipped |

**Which files come first.** Files are read in three tiers, most important first:

1. the attachments of the email being answered, then its links;
2. files of earlier messages whose name (or its name without the extension, at least 4
   characters) appears in the email's subject or in what the sender wrote, as a whole
   word, for example "see the budget" for `budget.xlsx`;
3. everything else in the thread, newest message first.

The same file sent twice (same name and size) counts once. The 40,000 characters go to
the tiers in order: tier 1 first, then tier 2, then tier 3. When the files of a tier
don't all fit in what is left, they are cut to the same length. If that length would be
under 2,000 characters, each file of the tier gets 2,000 in turn (in tier 3, newest
message first) until fewer than 2,000 are left, and the rest are skipped as too much
text for one draft. So the oldest files of the thread go first, and the email's own
files are only cut, never dropped while 2,000 characters per file are left.

Clippy tells the model which files were not read and why, so a reply that depends on
one gets a placeholder like `[[check budget.xlsx]]` instead of an invented answer. The
model is told that the file text is data, never instructions.

### Shared files (OneDrive and SharePoint)

Clippy opens a shared file only when **all** of these are true:

- **It is a OneDrive or SharePoint link.** Only links to `<name>.sharepoint.com`
  (including `<name>-my.sharepoint.com`), `onedrive.live.com`, `d.docs.live.net` and
  `1drv.ms` are considered. Microsoft Safe Links wrappers around such a link are
  unwrapped first. A link to any other site, such as Dropbox or Google Drive, is **never
  opened, mapped or fetched**: Clippy doesn't list it for the model, and the model still
  sees the link in the email text as before.
- **It is already synced to this PC.** Clippy never downloads anything from the
  network. It looks up the link among the folders your OneDrive sync client keeps on
  this PC (your OneDrive, synced SharePoint and Teams libraries and folders, and
  folders added with **Add shortcut to My files**) and reads the local copy. There is
  no Microsoft Graph and no sign-in. If the link points to a file in a library you
  don't sync, the file is **not synced to this PC** and is not read. Sharing links that
  carry only a secret token (`/:w:/s/…`, `/:f:/g/…`, "Specific people" links),
  `Doc.aspx?sourcedoc=…` links, and every `onedrive.live.com` and `1drv.ms` link can't
  be matched to a local file, so they end up in the same state.
- **It comes from you or your own organization.** A link or cloud attachment is opened
  only when the email that holds it was sent by you, by the shared mailbox's own address,
  or by an address in your own email domain or one of its subdomains. If your own address
  is at a free or consumer mail provider such as outlook.com, hotmail.com, gmail.com,
  yahoo.com, icloud.com or GMX, the domain is not "your organization": only links you
  sent yourself (or the shared mailbox's own address) are read. A stranger could
  otherwise send a link to a file in *your* OneDrive and get its content into a draft
  addressed back to them. Normal attachments are always read, whoever sent them.
- **Only the text each person wrote.** Links are taken from the part each sender
  wrote themselves. Links in quoted replies (including lines starting with `>`), in
  forwarded mail or in signatures are ignored, because they belong to someone else.

**Files On-Demand.** If your OneDrive keeps the file "online only", opening it makes
**your own OneDrive client** download it, with its own sign-in. Clippy itself makes no
network request. The size check happens first, and the download time counts against the
10 and 20 second limits. A file that doesn't arrive in time is skipped as too slow.
Files-on-demand placeholders are not treated as shortcuts or links, so they are allowed.

Clippy reads only through the normal Windows file path rules ([file
access](security.md#file-access)): it never follows a shortcut or junction out of the
synced folder.

### The yellow notes

If Clippy couldn't read a file that you would expect it to read, the draft gets a
yellow-highlighted line at the top, just under the [reason line](#the-reason-line), in
the same style. The notes are Clippy's own text, not the model's. They are for the
email's own files and for earlier files that the email mentions by name (tiers 1 and
2), never for the rest of the thread. File names are cleaned, links in them are shown as
`[link]`, and a name is cut at 80 characters.

| Note | Meaning |
|---|---|
| `Clippy: Couldn't read the shared file <name> (not synced to this PC).` | The link or cloud attachment doesn't match a file in your synced OneDrive/SharePoint folders, or the file isn't there |
| `Clippy: Couldn't read the shared file <name> (Clippy only opens shared files from people in your organization).` | The email came from outside your organization |
| `Clippy: Couldn't read <name> (the earlier email is no longer in your mailbox).` | The message with that file was deleted or archived. Only for a file the email mentions |
| `Clippy: Couldn't read <name> (it's password-protected).` | An encrypted PDF or Office file |
| `Clippy: Couldn't read <name> (larger than 25 MB).` | Over the size limit |
| `Clippy: Couldn't read <name> (no text in it, maybe a scan).` | A PDF with no text |
| `Clippy: Couldn't read <name> (Clippy can't read .<ext> files).` | One of the [formats it can't read](#formats), such as `.doc`, `.xls`, `.rtf` or `.zip` |
| `Clippy: Couldn't read <name> (the file seems damaged).` | A corrupt file, a file whose content doesn't match its type, or a read that failed |
| `Clippy: Couldn't read <name> (it took too long).` | A time limit was hit. While one PDF that took too long is still being processed, other PDFs are skipped with this note too |
| `Clippy: Couldn't read <name> (too much attachment text for one draft).` | The file had no share of the 40,000 characters or was over the 20 files |
| `Clippy: Read only the start of <name> (it's long).` | The file was cut by the page, row, slide or character limits, or by the draft total |

At most **5** notes are shown. More are replaced by one line,
`Clippy: <n> more files couldn't be read fully.` (`1 more file couldn't be read fully.`
for one). The same note is shown once. Images, file types Clippy has no note for, and
links to sites other than OneDrive and SharePoint get **no note**. Like the reason
line, the notes are part of the draft as Clippy saved it: **delete them before
sending**, and deleting them counts as editing the draft, so Clippy leaves that draft
alone afterwards. When Clippy re-checks a draft it shows the model neither the reason
line nor the notes. The notes appear whether or not the reason line is on.

### What you see in Activity

When any file was considered, the `Draft` event gets a suffix:
`AI reply draft created: Re:: Budget (attachments: 3 read, 1 not read)`. "Read"
includes files read only in part. Activity and the log hold **counts only**: never a
file name, a link, a path, a reason or any text.

### Privacy

- The text goes to Claude (through your Claude Code or API key) for **that one draft**,
  the same way the email text does. Anything an attachment contains, such as personal
  data, goes with it. That is why consent mentions it.
- The text is **never stored** by Clippy: not in the index, the settings, the logs or
  the Activity window, and there is no cache. The same attachment is read again for the
  next draft in a thread. Attachment contents are still never indexed, only the file
  names.
- To read an Outlook attachment, Clippy saves a copy for a moment in a private folder,
  `%LOCALAPPDATA%\ClippysRevenge\ai-files\`, that only your Windows user can use. The
  copy is read into memory and deleted at once. Whatever is left (for example after a
  crash) is deleted when Clippy starts. Synced files are read in place and not copied.
- Nothing is ever sent, moved or deleted in your mailbox by this. Clippy doesn't
  display, save or change the mail and attached emails it opens.
- A draft that used attachment text is **not** used for
  [learning from your edits](#learning-from-how-you-edit-drafts): Clippy doesn't keep
  its text at all, so file text can't end up in your writing style.

### Turning it off, and the one-time notice

Untick **Read attachments and synced shared files for drafts** in Settings, **AI
drafts**, to stop reading files. Drafts then carry attachment names only, as in version
0.1.0. Nothing about attachments is then asked of Outlook, the index or the registry.

If you gave consent before version 0.1.1, you are not asked again. Instead, once, at
the first pass after the update with the setting on, an Activity entry and a balloon say:

> AI drafts now also read attachments and shared files synced to this PC, and send their text to Claude, to write better replies. You can turn this off in Settings → AI drafts.

New consents already name it in the [consent text](#consent-and-what-is-sent).

### Token tally

Longer requests cost more of your Claude plan or API budget, so Clippy shows what
today's AI calls used. While you have given consent, a line like this is shown:

```
Today: 18 AI calls, ~210k tokens (in 190k / out 20k)
```

- **Where:** the first, greyed-out line of the tray's **AI drafts** submenu (refreshed
  when you open it) and under the state line in the [Activity window](activity.md).
- **What it counts:** every Claude call of the day (since midnight, your time): automatic
  drafts, manual drafts, re-checks, rescans and writing-style updates, whether or not a
  draft came out of it. `in` is the input tokens including cached ones, `out` is the
  output, and the `~` number is their sum. Calls that never started or were refused
  (usage limit reached, not signed in, Claude Code missing) aren't counted.
- **Forms:** `Today: no AI calls`; `Today: 3 AI calls, tokens unknown` when Claude
  reported no token numbers; and `…, 2 without token data` added when only some calls
  lack them. Large numbers are rounded: `1.2k`, `210k`, `1.2M`.
- **What is stored:** only counts, in your own mailbox's index, kept for 30 days. No
  prompt, answer or mail text. The tally is separate from **Daily limit** (style
  updates are shown in it but don't count toward the limit). The log line after a pass
  also ends in `, tokens in <n> out <n>`.

This is different from the `Today: <n> of <cap> Claude calls, <d> drafts` line in
Settings, which counts calls against your daily limit.

### Drafts and Deleted Items in other languages

Clippy skips mail in **Drafts** and **Deleted Items** when it picks related emails and
earlier files. Since version 0.1.1 it recognizes these two folders by their Outlook
role, so it also works when Outlook shows them under a translated name. Folders that
Clippy indexed **before** version 0.1.1 under their translated name keep their old
entries until you add the folder again or press **Rebuild index…**. The old entries
stay marked with the translated name, so the exclusion doesn't apply to them.

## The reason line

Every AI draft starts with a yellow-highlighted line that says why Clippy drafted it, for
example:

> <span style="background-color:#ffff00">Clippy: Ricky asked about the Saturday bus; this answers that.</span>

- Claude writes it as one short sentence in the language of the reply: what the email
  asks and what the draft answers. It is **at most 160 characters**; a longer one is cut
  with `…`.
- Links never appear in it. A web address, `mailto:` or `www.` text is shown as
  `[link]`, because the line carries Clippy's authority and a malicious email must not
  be able to put a link there.
- The setting is **Start each draft with a highlighted note on why it was drafted** in
  Settings, **AI drafts**, **on by default**. When off, no line is asked for or added.
- It is added above the meeting times and the reply text, in new drafts, manual drafts,
  replaced drafts and rescans. If Clippy couldn't read a file, its
  [yellow notes](#the-yellow-notes) follow right below it.

**Delete the line before you send.** It is meant for you only. If you forget and send
it, Clippy strips such a `Clippy:` line from your own sent mail whenever it later shows
that mail to Claude (earlier messages in a thread, related emails, and the samples for
your writing style), so it doesn't learn it as part of your style. Mail from other people
is never changed.

**Deleting the line counts as editing the draft.** Clippy's check for outdated drafts
only touches a draft that is exactly as Clippy saved it. Once you remove the line (or
change anything else), Clippy leaves that draft alone: it won't replace it or move it to
Deleted Items. See [Outdated drafts](#outdated-drafts).

## Your writing style

Clippy learns your style from your own sent mail and keeps it in a Claude Code
**skill** file, `my-email-style`.

- It samples up to 200 emails from the last 12 months of your Sent Items, a mix of
  internal and external, short and long, newest first, at most 3 per recipient, each
  cut at 1,500 characters.
- It uses only **your own text**. Quoted history and signatures are cut, and
  automatic replies and meeting responses are left out.
- **Mail Clippy drafted for you is excluded** from the samples, even after you edited
  and sent it. So are replies to mail that Clippy drafted an answer for, so the style
  doesn't learn from its own output. How you *edited* those drafts is used separately,
  as described in [Learning from how you edit drafts](#learning-from-how-you-edit-drafts).
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
when at least 20 new sent emails have appeared.

**The first skill is built as soon as your Sent Items are indexed.** The style comes
from Clippy's index, so Clippy indexes **Sent Items first**, before the Inbox, and
waits until that is complete. On the first run, drafting then starts and builds the
first skill right away, without waiting for the Inbox backlog. Until then, automatic
drafting doesn't build a style from a half-indexed Sent Items. If the first attempt
finds too few sent emails or fails, the next automatic attempt is 24 hours later;
**Regenerate** in Settings doesn't wait for any of this.

**Where the style comes from (`StyleSource`).** Every draft, whether automatic,
manual, rescan or re-check, uses one of three sources, in this order:

1. **Your file**: `%USERPROFILE%\.claude\skills\my-email-style\SKILL.md`, if it can
   be read and is at most 64 KiB. This is the normal case, and your edits count.
2. **Clippy's own copy** in `%APPDATA%\ClippysRevenge`, if your file is missing or
   unreadable. The Activity window says `Writing style: your SKILL.md is missing or
   unreadable, so drafts use Clippy's saved copy. Regenerate it from AI drafts, or
   fix the file.`
3. **None**: drafts use "write plainly, briefly and politely". The Activity window
   says `Writing style: SKILL.md not found or unreadable — drafts use no style
   guide. Regenerate it from AI drafts.`

These messages appear when the source **changes**, not for every draft. When your file
works again you see `Writing style: using your SKILL.md again.` Style generation doesn't count
toward the daily limit. In Settings you also have **View** (read-only), **Regenerate**
(runs now and takes a few minutes) and **Open in editor**. The status reads
`Generated 2026-11-13 from 200 sent emails`, `Edited by you` or `Not generated yet`.

### The meeting-times section of the skill

The end of `SKILL.md` always has a section, **Offering meeting times**, written by
Clippy between the markers `<!-- clippy:meeting-times v2 -->` and
`<!-- /clippy:meeting-times -->`. It teaches your **interactive** Claude Code
sessions the same habit: to get times from [`find_free_times`](tools.md#find_free_times)
instead of making them up, to paste its `offer_text` unchanged, one option per line,
and to set `offer_cet` when writing to someone in Europe. It also says to leave the
options in the ranked order and to say that you will suggest times if none are found.
When you pick one of the offered times (for example "make the invite for option 2"),
the session is to call [`create_meeting_draft`](tools.md#create_meeting_draft) with
`reply_to_id` set to the email that asked for the meeting, and never to create an
invite unless you ask. A short paragraph on how **you** usually
introduce meeting options is taken from your own sent mail.

- A **new or regenerated** skill always gets the section.
- A skill with no section, or with an older version of it (`v1`), gets the current
  section automatically, once, when Clippy starts, with no Claude call, as long as
  **you haven't edited the file**. The Activity window then shows `writing style: added
  how to offer meeting times`.
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

## Learning from how you edit drafts

Clippy's drafts are a starting point: you shorten an opening, drop a phrase, change a
sign-off, and send. Since version 0.1.1 Clippy can notice those changes and feed them
into your [writing style](#your-writing-style), so later drafts need fewer edits. It
costs **no extra Claude call**: the edits are given to the writing-style update that
already runs once a week. Nothing is read from, or changed in, the mail you sent.

The setting is **Learn from how I edit Clippy's drafts**, in Settings, **AI drafts**,
**Writing style** group, **on by default**.

### What Clippy records when it saves a draft

Each time Clippy saves an AI draft (an automatic draft, **Draft a reply to the selected
email**, a [rescan](#rescan-emails-from-the-last-24-hours) or a replacement in the
[outdated-draft check](#outdated-drafts)), it keeps a copy of the draft's text in its
own local index:

- Only **the reply text**: what Claude wrote, including any
  [meeting times](#meeting-times) Clippy offered in it (the text is taken just before
  the draft is saved). The yellow [reason line](#the-reason-line), the yellow
  [notes](#the-yellow-notes) and your signature are left out. So deleting or changing
  an offered time before you send counts as an edit. The style update is told to
  ignore one-off content such as dates, so this doesn't change your style.
- Addresses, links and phone numbers are replaced by `<email>`, `<link>` and `<phone>`,
  and the text is cut to **1,500 characters**.
- A copy is kept only when the setting is on, the email has an internet message id (so
  the sent reply can be matched later) and the draft was **not** built with attachment
  text. A draft that used the text of an attachment or a synced file is never kept, so
  file contents can't reach your writing style. A draft where every attachment was
  skipped (nothing read) still counts.
- Drafts made with the `reply_draft` tool are never recorded. An outdated-draft check that keeps or removes a draft saves nothing new.
- A draft for a shared mailbox you turned on is recorded in your own mailbox's index.

If a copy can't be saved, the draft itself is not affected. The log says only that
`AI drafts: couldn't record a draft's text (<exception type>).`

### How Clippy finds what you sent

After each sync of your own mailbox, and at most **every 5 minutes**, Clippy looks at
the mail in your index that you sent in the last 30 days (up to 500 per run, newest
first). It compares only the index; it asks Outlook nothing. A sent message is
matched to a recorded draft when:

- it is in Sent Items (or a folder below it) and still carries Clippy's `ai` marker
  (Outlook keeps it when a draft is sent),
- its "in reply to" id equals the internet message id of the email the draft answered, and
- the draft was recorded before the message was sent.

Each sent reply is matched with the draft it came from. A recorded draft for that email
that you never sent (for example one that Clippy replaced) loses its text. A reply that
you wrote from scratch has no marker and is never matched. Sent mail without any
recorded draft (for example, from before version 0.1.1, or when the draft used
attachment text) is simply ignored.

### Sent unchanged, or edited

Clippy prepares your sent text the same way as the draft (your own text only, without
the quoted email, your signature and any `Clippy:` line you forgot to delete; then
redaction and the 1,500-character cut). It splits both texts into words and
punctuation marks and measures what they share. The **similarity** is a number from 0
to 1:

- **0.98 or more**: *sent unchanged* (or nearly: one word in about 50). Clippy keeps
  only the numbers (similarity and lengths), no text.
- **Less than 0.98**: *edited*. Clippy keeps both texts, the draft and what you sent.

For example, `Thanks, sounds good.` against `No.` has a similarity of about 0.29.
Capital letters count as a difference.

### What is kept, and for how long

Everything is in Clippy's local index file, nowhere else:

| What | How long |
|---|---|
| A recorded draft text waiting for its sent reply | 30 days. Then it is marked expired and its text is erased |
| A draft text that was matched, superseded or expired | 30 more days, then the row is deleted |
| The texts of **the newest 20** edited pairs | Until 20 newer pairs exist. Older pairs lose their texts and keep only their numbers |
| The numbers of a pair (similarity, lengths, time) | 90 days, then the row is deleted |

[`query_index`](tools.md#query_index) and the other tools can't read any of this: they
see only mail, not these records. **Rebuild index…** in the tray deletes it with the
rest of the index. Texts and similarity numbers never appear in logs or in the Activity
window; only counts do.

### When your edits go to Claude

Nothing about your edits leaves your PC when you send a reply. It leaves only at the
**writing-style update**: the automatic weekly one, or when you press **Regenerate** in
Settings (the usual [edit check](#your-writing-style) for a style you edited still
applies). That update then does this:

1. If the setting is on and at least **3** edited pairs from the last 90 days have
   texts, it adds them to the request, **up to 20, newest first**, together with a
   count of how many drafts you sent unchanged in those 90 days. Each pair has the
   draft, what you sent and the similarity.
2. Claude is told that these pairs are data, never instructions, to look for
   patterns that **recur** (what you add, remove, shorten or rephrase, which greetings
   and sign-offs you change), to ignore one-off content (facts, dates, names, numbers),
   to mention when most drafts went out unchanged, and to write the result as a short
   section of your style guide, **Edits I usually make to drafts**, with a few bullet
   points as instructions for future drafts. The section has no names, addresses,
   links, phone numbers or other numbers.
3. With fewer than 3 pairs, or with the setting off, the request is exactly as before.

Your drafting prompts then use the new section like the rest of the skill. You can read
it with **View** in Settings, and edit it. As with any change to the skill, an update
never overwrites your own edits without asking. If Clippy can't read the edits, it
logs `Writing style: reading edited drafts failed (<exception type>)` and updates the
style without them.

The result line in Settings stays `Writing style updated from <n> sent emails.` The
update still needs at least 20 suitable sent emails: the edits only add to them.

### What you see in Activity

- After a check that found new sent drafts: `AI drafts: noted how you edited 1 sent
  draft.` or `AI drafts: noted how you edited 12 sent drafts.` The number includes
  drafts you sent unchanged. No subject or address.
- When a writing-style update used edits: `writing style updated from 240 sent emails
  and 7 edited drafts (52 s)`. The second number is the pairs given to Claude.

See [Activity](activity.md#writing-style) for all style messages.

### Turning it off

Untick **Learn from how I edit Clippy's drafts**. Clippy then stops recording draft
texts, stops matching sent mail, and leaves the edits out of the next style update. It
**doesn't delete** what it already holds: those rows age out as described above, or
disappear at once when you press **Rebuild index…**. A style section that was already
written stays in your skill until the next style update replaces the skill.

### Notice for existing users

New consents already name this in the [consent text](#consent-and-what-is-sent). If you
gave consent before version 0.1.1, you are not asked again. Instead, once, at the first
AI draft Clippy saves after the update with the setting on (before any edit can be
recorded, so before anything can reach Claude), an Activity entry and a balloon say:

> AI drafts now learn from how you edit Clippy's drafts before sending. The next writing-style update sends up to 20 of your edited replies (addresses, links and phone numbers removed) to Claude. You can turn this off in Settings → AI drafts.

### Privacy in short

- Stored only in your local index, as redacted own text, capped and purged as above.
  Never in settings, logs, Activity, or any tool result.
- Leaves your PC only inside the writing-style update, up to 20 pairs, redacted.
- No Outlook call is involved and nothing in your mailbox changes.
- The model still has no tools. See also [Security](security.md#ai-drafts-data-flow).

## Troubleshooting

The status line under **Use** and the Activity window tell you what is wrong. Fix it,
then press **Check**.

| Message | What to do |
|---|---|
| `✗ Claude Code wasn't found. Install it from https://claude.com/claude-code, sign in once in a terminal, then press Check.` | Install Claude Code, or set **Claude Code path (optional)** to `claude.exe` or `cli.js`. A `.cmd`, `.bat` or `.ps1` shim isn't supported |
| `✗ Claude Code <version> is too old for AI drafts. Run "claude update", then press Check.` | Run `claude update` in a terminal |
| `✗ Claude Code is installed but not signed in. Open a terminal, run claude and sign in, then press Check.` | Run `claude` and sign in yourself. Clippy can't do it |
| `✗ Claude Code didn't answer (version). Press Check to try again.`, also with `help` or `auth status` | Press **Check** again. If it repeats, run `claude --version` in a terminal |
| `✗ The check didn't finish. Press Check to try again.` | Something unexpected went wrong during the check. Press **Check** again |
| `✓ Claude Code <version> · signed in (...)` plus `· update Claude Code for best results` | Works. Updating lets Clippy keep your own CLAUDE.md, hooks and plugins out of drafting calls |
| `✗ No API key saved.` | Paste your key and press **Save key** |
| `✗ The API key was rejected (HTTP 401).` | The key is wrong or revoked. Save a new one |
| `✗ The model "<model>" isn't available with this key.` | Change **Model** |
| `✗ api.anthropic.com didn't answer.` | Check your network and try again |
| `AI drafts: Claude isn't signed in` (Activity, error state) | Sign in again in a terminal, then press **Check**. Drafting resumes |
| `AI drafts: Claude's usage limit was reached. I'll try again at 14:05.` | Wait. It retries by itself |
| `AI drafts: a Claude call failed (<kind>). I'll try again in 4 min.` | Usually temporary. If it keeps failing, press **Check** |
| `AI drafts: daily limit of 30 reached. I'll continue tomorrow.` | Raise **Daily limit** or wait |
| `Rescan stopped (...) after 5 of 12 emails: ...` | See [Rescan](#rescan-emails-from-the-last-24-hours) for the reasons. Fix the cause and rescan again |
| `AI drafts: an outdated draft couldn't be moved to Deleted Items. It's still in Drafts.` | Clippy couldn't open that mailbox's Deleted Items. Delete the old draft yourself if you want |
| `AI drafts: no draft for the email received ...: Claude's confidence 0.55 is below your minimum of 0.60.` | Draft it by hand from the tray, or lower **Minimum confidence**. See [Emails Clippy decides not to draft](#emails-clippy-decides-not-to-draft) |
| `Clippy: Couldn't read the shared file <name> (not synced to this PC).` or another yellow `Couldn't read …` note at the top of a draft | Clippy couldn't read a file. See [The yellow notes](#the-yellow-notes) and [Why couldn't Clippy read my shared file?](faq.md#why-couldnt-clippy-read-my-shared-file) |
| `[[check budget.xlsx]]` in a draft | The model was told the file wasn't read and left a placeholder. Open the file yourself |
| `[[add meeting times: ...]]` in a draft | Clippy couldn't offer times. See [Meeting times](#meeting-times) |
| `AI drafts: a reply draft couldn't be saved in Outlook. I'll try again later.` | Check that Outlook is running and Drafts is available |
| `Select one email in Outlook first.` | Manual draft: select one email |
| `That email isn't in your mailbox or an enabled shared mailbox.` | Manual draft: turn the shared mailbox on first |
| `That mailbox isn't indexed yet.` | Manual draft: wait until the shared mailbox's first backfill is done |
| `Claude isn't ready: ...` | Manual draft or rescan: fix the status shown, then try again |
| `Today's AI draft limit (30) is used up.` | Manual draft or rescan: raise **Daily limit** or wait until tomorrow |
| `I couldn't draft that reply: ...` | The reason follows, for example Claude's usage limit, or that Outlook isn't running |
| `I couldn't start the rescan: ...` | The reason follows. Fix it and rescan again |
| `Couldn't change AI drafts: ...` | A tray item couldn't save the settings. The reason follows |
| `AI drafts need the email index, which couldn't be opened. Check the tray icon for the reason.` | The index failed to open. See [Troubleshooting](troubleshooting.md) |
| `Clippy doesn't know your email address yet. Start Outlook, wait for the first sync, then try again.` | Needed to write the style. Start Outlook and let it sync |
| `Only 12 sent emails found. Clippy needs 20.` | Not enough sent mail to learn from yet |
| `There is no writing style skill yet. Press Regenerate to create one.` | Press **Regenerate** |

If an email gets no draft, the usual reasons are: it arrived before you turned AI
drafts on, it is older than the age limit, it matched a [rule](#rules), or Claude
decided no reply was needed or wasn't confident enough (the Activity window says so
in the second case).

Logs and the Activity window never contain mail text, prompts, the style skill,
attachment text, attachment file names or links, or Claude's answers.
