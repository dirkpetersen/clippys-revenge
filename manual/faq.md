# FAQ

## Does it work with the new Outlook?

No. It needs **classic Outlook**, because it drives Outlook through COM. The new
Outlook has no COM interface.

## Does it work with IMAP or POP accounts?

Not supported. An Exchange or Microsoft 365 mailbox is required.

## Can it send email?

No. It saves drafts and you send them from Outlook. The only thing that can send is
a [meeting invite](calendar.md#meeting-invites), and only after you click **Send**
in a confirmation dialog, unless you set invites to draft only or off.

## Can it delete or move my mail?

No. It never deletes or moves email.

## Why do reply subjects start with "Re::"?

It is intentional. Reply drafts always start with `Re:: ` (double colon).

## Does my mail go to the cloud?

Clippy has no cloud component. It runs on your PC and listens on `127.0.0.1`.
What Claude Code then sends to its model is the text of the mail it reads, so
treat that as you would any use of an AI assistant with your mail.

## Do I need an app registration or admin consent?

No. There is no Graph API, Entra app registration or OAuth. If Outlook can open
the mail, Clippy can use it.

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

Run the install one-liner again. See [Install](install.md).

## How do I uninstall?

See [Uninstall](install.md#uninstall).

## Why does SmartScreen warn me?

The exe is not code-signed yet. The installer verifies its checksum.

## Where are the logs?

`%LOCALAPPDATA%\ClippysRevenge\logs`. Use the tray's **Open log folder**.
