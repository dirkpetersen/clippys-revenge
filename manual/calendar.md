# Calendar and meetings

Three tools work with your calendar: [`list_events`](tools.md#list_events),
[`find_free_times`](tools.md#find_free_times) and
[`create_meeting_draft`](tools.md#create_meeting_draft). They read Outlook live, so
Outlook must be running. Clippy never sends anything: a meeting is saved unsent and you
send it from Outlook.

## Listing events

`list_events` returns events in a window, with recurring events expanded, at most
500. Without arguments it covers the next 7 days. Pass `mailbox` to read a
[shared mailbox's](indexing.md#shared-mailboxes) calendar.

## Finding a free time

`find_free_times` reads Outlook free/busy for you and the attendees, combines it
with your working hours, and suggests the first free blocks that are long enough.
Nothing is booked.

A typical flow:

1. You paste a list of attendees, as copied from an Outlook address line.
2. Claude calls `find_free_times` and shows suggestions such as
   `1. Tue 10/15 1-3 PT`.
3. You say "book number 2". Claude passes that suggestion's `start` and `end` to
   `create_meeting_draft`.

Things to know:

- People whose free/busy can't be read, such as people outside your Exchange
  organization, are listed as unknown and ignored. Check with them yourself.
- Working hours, business-days-only, the time-zone label and whether tentative
  counts as busy come from [Settings, Scheduling](tray.md#scheduling). Claude can
  override the hours and business days per call.
- Suggestions are whole free blocks, in local time. They are ranked: times right
  before or after your own meetings come first, then times next to tentative
  meetings, then the rest.
- Besides the blocks, the result has **offers** of exactly the meeting length and an
  `offer_text` with one option per line, ready to paste into an email. For someone
  in Europe, Claude sets `offer_cet` and each line also shows the time in CET, with
  the options inside European office hours first. See
  [`find_free_times`](tools.md#find_free_times).
- Clippy's [AI drafts](ai-drafts.md#meeting-times) use the same offers when an email
  asks for a meeting.

## Meeting drafts

`create_meeting_draft` saves a meeting in your calendar **without sending it**.
Clippy never sends anything, neither mail nor meetings. Outlook's object model can't
add a Teams or Zoom link, so you add the link yourself and send the invite from
Outlook.

The workflow:

1. You ask Claude Code to book a time, for example "make the invite for option 2".
   Claude calls `create_meeting_draft` with the attendees, subject, start and end.
2. The meeting is saved **unsent** in your calendar with the orange category
   **Clippy draft invite**.
3. If the invite answers an email, Claude passes `reply_to_id` and Clippy also saves
   a [note in Drafts](#the-note-in-drafts).
4. The tray icon shows a [yellow badge](tray.md#attention-badge) with the number of
   draft invites, and the tray menu has **Draft invites (n)**.
5. You open the meeting from that list, or from your calendar. Add the Zoom or Teams
   link and click **Send** in Outlook.
6. Within a few minutes Clippy sees that the invite has gone out and removes its
   category. Any other category you set stays, and the badge count goes down.

Before anything is created, every recipient must resolve in Outlook. If one doesn't,
nothing is created.

### Invite mode

Set it in [Settings, Scheduling](tray.md#scheduling), **Meeting invites**:

| Mode | What happens |
|---|---|
| **Draft only** (default) | `create_meeting_draft` saves the meeting unsent |
| **Disabled** | The tool refuses. Claude is told to ask you to enable it |

Older versions had a **Send** mode with a confirmation dialog. It is gone. A saved
**Send** now loads as **Draft only**, and the confirmation timeout is ignored.

### The category

Every meeting draft gets the category **Clippy draft invite**. If your category list
doesn't have it yet, Clippy adds it once, in orange. It never changes a category of
that name that you already have, and never removes it from your list.

When you have sent the meeting, Clippy removes only this category and saves the
meeting. Saving a category change doesn't send anything. If you removed the category
yourself, Clippy changes nothing. It skips a meeting while you have it open in
Outlook and tries again later.

### Draft invites in the tray

**Draft invites (n)** is in the [tray menu](tray.md#tray-menu) while at least one
meeting draft is waiting. Each line shows the day, time and subject. Click a line to
open the meeting in Outlook.

Clippy checks the list when you open the menu and after sync cycles, at most every
five minutes. A meeting you deleted is dropped from the list. A meeting that ended
more than 30 days ago and was never sent is dropped too, but it keeps its category.

While Outlook isn't running, the lines are greyed out and the last line says
**Start Outlook to open them**.

### The note in Drafts

When Claude passes `reply_to_id` (the `id` of the email that asked for the meeting),
Clippy saves a reply draft to that email. The reply starts with a highlighted note:

> [[Draft invite saved in your calendar: Tue 10/15 10:00–11:00 PT "Project sync".
> Open it from the Clippy tray (Draft invites) or your calendar, add the Teams or
> Zoom link and send it. Delete this note before you send this reply.]]

Below the note is an empty paragraph for your own text. Delete the note before you
send the reply. If an [AI draft](ai-drafts.md) already answers the email, it is left
alone, and the note is a second reply draft.

If the note can't be saved, the meeting is still saved and the result has a
`note_error` that says why.

### Shared mailboxes

With `mailbox`, the meeting is saved in that shared mailbox's calendar, and free/busy
checks include that mailbox instead of yours. The category is set on the meeting, but
Outlook shows it without the orange color unless that mailbox's category list has it.