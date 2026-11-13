# Changelog

The full list of releases, with downloads, is on
[GitHub](https://github.com/dirkpetersen/clippys-revenge/releases).

## v0.0.5

2026-11-13

- AI drafts: Clippy can draft replies to new mail in your writing style with Claude
  Code or an Anthropic API key. Off by default, asks for consent, saves drafts only,
  never sends, and the model gets no tools. See [AI drafts](ai-drafts.md).
- Writing-style skill built from your own sent mail, saved as
  my-email-style in your Claude Code skills folder. Your edits are never
  overwritten automatically.
- New tray submenu **AI drafts** and a Settings tab with the same name.

## v0.0.2

Released as a full (latest) release, so the installer and
`releases/latest` links pick it up.

- The installer and the README use the `releases/latest` download.
- One-line install: paste `irm ... | iex` into PowerShell.
- `install.ps1 -WhatIf` downloads and verifies the release without installing.
- Never deletes email: this is now a documented hard rule, checked by a test that
  scans the source.
- Reconcile refuses mass deletes from the index when Outlook's ids don't match the
  index.

## v0.0.1

First release.

- Tray app with a local MCP server on `127.0.0.1`, protected by a bearer token.
- Local SQLite full-text index of your mailbox and shared mailboxes.
- Tools: `search_emails`, `get_message`, `get_attachment`, `save_attachments`,
  `create_draft`, `reply_draft`, `list_events`, `find_free_times`,
  `send_meeting_invite` and `index_status`.
- Tray menu, Settings and Activity windows, and a confirmation dialog for meeting
  invites.
- `install.ps1` installer and uninstaller.
