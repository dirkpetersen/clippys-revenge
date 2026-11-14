# FAQ

## Does it work with the new Outlook?

No. It needs **classic Outlook**, because it drives Outlook through COM. The new
Outlook has no COM interface.

## Does it work with IMAP or POP accounts?

Not supported. An Exchange or Microsoft 365 mailbox is required.

## Can it send email?

No. Clippy never sends anything, neither mail nor meetings. It saves drafts and
you send them from Outlook. Meeting invites are saved unsent in your calendar too.
See [Meeting drafts](calendar.md#meeting-drafts).

## Why can't Clippy send a meeting invite?

Outlook's object model can't add a Teams or Zoom link, and you would add one by hand
anyway. So Clippy saves the meeting unsent, marks it with the orange category
**Clippy draft invite**, and lists it under **Draft invites** in the tray. You open
it, add the link and send it. Clippy then removes its category.

## What is the yellow number on the tray icon?

The [attention badge](tray.md#attention-badge). It counts meeting drafts waiting for
you to add a link and send them. It disappears when you have sent them all.

## Can it delete or move my mail?

No. It never deletes or moves email.

## Why do reply subjects start with "Re::"?

It is intentional. Reply drafts always start with `Re:: ` (double colon).

## Can it write replies for me?

Yes, if you turn on [AI drafts](ai-drafts.md). Clippy then asks Claude to draft
replies to new mail in your style and saves them in Drafts. It never sends them. It
is off by default and asks for your consent first.

## Will the AI draft offer meeting times?

Yes, when an email asks for a meeting and **Offer meeting times when an email asks
for a meeting** is on (the default). Clippy picks the times from your calendar itself,
the model never sees them, and each option is its own line that you can delete. For
senders in Europe it adds the time in CET. See [Meeting times](ai-drafts.md#meeting-times).

## Why does a meeting time say CET in summer?

The label is always "CET" for Central European time, also while Europe is on summer
time. The time itself uses the real offset (+02:00 in summer), so the number is right.

## Why did a draft come out without a CET time for a sender in Europe?

Clippy guesses where the sender is from the `Date` header and the address ending, and
can miss, for example for a `.com` address in winter UK time. See
[Senders in Europe](ai-drafts.md#meeting-times). You can add or delete the CET part
by hand.

## An email got no draft and the Activity window says "low confidence". What now?

Claude wasn't sure enough that a reply was needed, below your **Minimum confidence**.
Such an email is decided once and not retried. To get a draft anyway, select the email
in Outlook and use **Draft a reply to the selected email** in the tray menu. See
[Emails Clippy decides not to draft](ai-drafts.md#emails-clippy-decides-not-to-draft).

## Does AI drafting send my mail to Anthropic?

Yes, while it is on: the email being answered, thread excerpts, attachment names and
your style guide go to Claude through your own Claude Code (or your API key).
Attachment contents never do. See
[what is sent](ai-drafts.md#consent-and-what-is-sent).

## Does Clippy sign in to Claude or read my Claude login?

No. You sign in to Claude Code yourself in a terminal, and Clippy only asks it
whether you are signed in. It never reads Claude Code's stored credentials.

## Why didn't a mail get a draft?

It may have arrived before you turned AI drafts on, be older than the age limit, be a
newsletter or automatic mail, match a never-draft rule, or not need a reply. See
[Rules](ai-drafts.md#rules).

## Is the writing-style file safe?

It is a Claude Code skill in %USERPROFILE%\.claude\skills\my-email-style, so your
interactive Claude Code sessions can load it. Review, edit or delete it any time. See
the [security note](ai-drafts.md#your-writing-style).

## Does my mail go to the cloud?

Clippy has no cloud component. It runs on your PC and listens on `127.0.0.1`.
What Claude Code then sends to its model is the text of the mail it reads, so
treat that as you would any use of an AI assistant with your mail. The optional
[AI drafts](ai-drafts.md) feature also sends mail to Claude, with your consent.

## Do I need an app registration or admin consent?

No. There is no Graph API, Entra app registration or OAuth. If Outlook can open
the mail, Clippy can use it.

## Can Claude answer "who emails me the most?" or "how much mail do I get per month?"

Yes, in one call. Ask in plain language, for example "top 10 people who emailed me in
the last year, with counts" or "mail per month since January". Claude uses
[`email_stats`](tools.md#email_stats), which counts mail in the local index by
sender, sender domain, recipient, folder, month, week, weekday or hour. It returns
counts and names, never mail text, and works while Outlook is closed.

## Can Claude run its own searches with SQL?

Yes, with [`query_index`](tools.md#query_index): one read-only `SELECT` over three
views of the index, including full-text search with snippets. Nothing can be changed
and nothing outside those views can be read. See
[Security](security.md#statistics-and-sql-queries).

## Does Outlook have to be running?

For indexing, yes. Clippy never starts Outlook on its own. Searching the existing
index works while Outlook is closed. Tools that read live from Outlook (full
messages not yet indexed, attachments, drafts, calendar) need it running.

## Can Claude Code in WSL use it?

Yes, with WSL's mirrored networking. See [Connect Claude Code](connect.md#from-wsl).

## Can other computers or web pages reach it?

No. It binds to `127.0.0.1` only, requires a token, and rejects any request that
has an `Origin` header.

## How far back does search go?

12 months by default. Change it in Settings, Index (1 to 120 months).

## Are attachments searchable?

Attachment **file names** are searchable. Their contents are never indexed. To read
an attachment, Claude saves it with [`get_attachment`](tools.md#get_attachment) and
reads the file.

## Where does it save attachments?

`%USERPROFILE%\Downloads\Clippy's Revenge` by default. Claude can only save to
allowed folders, which you control in Settings, General.

## Can I use a shared mailbox?

Yes. Check it under tray, **Shared mailboxes**. See
[Shared mailboxes](indexing.md#shared-mailboxes).

## Does it start with Windows?

Yes by default. Turn it off with the **Start with Windows** item in the tray menu.

## How do I update?

By default Clippy checks GitHub once a week and installs a newer version at its
next start. To update at once, open **About Clippy's Revenge…** in the tray and
press **Update now**, or run the install one-liner again. See
[Updates](updates.md) and [Install](install.md).

## How do I turn updates off?

Settings, General, **Updates**: choose **Don't check** (no automatic checks) or
**Only tell me** (a balloon, no download). See [Updates](updates.md#turn-updates-off).

## Does Clippy send anything to GitHub?

Only the weekly update check and the download, as plain requests that carry just
the version number. No mail, no settings, no telemetry. See
[what is sent](updates.md#what-is-sent-to-github).

## How do I uninstall?

See [Uninstall](install.md#uninstall).

## Why does SmartScreen warn me?

The exe is not code-signed yet. The installer verifies its checksum.

## Where are the logs?

`%LOCALAPPDATA%\ClippysRevenge\logs`. Use the tray's **Open log folder**.
