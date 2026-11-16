# Calendar and meetings

Three tools work with your calendar: [`list_events`](tools.md#list_events),
[`find_free_times`](tools.md#find_free_times) and
[`create_meeting_draft`](tools.md#create_meeting_draft). They read Outlook live, so
Outlook must be running. Clippy never sends anything: a meeting is saved unsent and you
send it from Outlook.

## Listing events

`list_events` returns events in a window, with recurring events expanded, at most
500. Without arguments it covers the next 7 days (`days` can be 1 to 366). Each event
has its subject, start, end, all-day flag, location, organizer, whether it recurs and
its busy status. Pass `mailbox` to read a [shared mailbox's](indexing.md#shared-mailboxes)
calendar. It only reads; nothing is changed.

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
  counts as busy come from [Settings, Scheduling](settings.md#scheduling). Claude can
  override the hours and business days per call.
- Suggestions are whole free blocks, in local time. They are ranked: times right
  before or after your own meetings come first (within 15 minutes, on the same
  day), then times next to tentative meetings, then the rest, each group in date
  order. Free/busy can't tell a meeting from other busy time, so "next to a meeting"
  means "next to busy time". If your own calendar isn't checked (`include_me` off, or
  your free/busy can't be read), no time is ranked above another.
- At most 50 attendees, and at most 60 days ahead.
- The tool offers free time from now on, with no extra lead time. AI drafts add a
  lead time of their own; see [AI drafts](ai-drafts.md#meeting-times).
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

Set it in [Settings, Scheduling](settings.md#scheduling), **Meeting invites**:

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
meeting draft is waiting, and hidden when there is none. Each line shows the day,
time and subject, for example `Tue 11/18 10:00–11:00  Project kickoff`. Meetings in a
shared mailbox's calendar add the mailbox name. The list is sorted by start and shows
at most 15 lines, then a greyed `and <n> more`. Click a line to open the meeting in
Outlook. Clicking only opens it; it writes nothing. After you restart Clippy, lines
may read `Draft invite saved <day and time>` until the first check.

Clippy checks the list when you open the menu and after sync cycles, at most every
five minutes. A meeting you deleted is dropped from the list. A meeting that ended
more than 30 days ago and was never sent is dropped too, but it keeps its category.
If a meeting has gone from your calendar when you click it, a balloon says `That
draft invite is no longer in your calendar.` and the line is removed.

While Outlook isn't running, the lines are greyed out and the last line says
**Start Outlook to open them**. While Outlook is not responding they are greyed out
without that line.

### The badge on the tray icon

While draft invites are waiting, a small yellow badge with their number sits in the
top-right corner of the tray icon: `1` to `9`, then `9+`. The tooltip adds `· 2 draft
invites need your attention`. The badge keeps its last count while Outlook is closed,
because the meetings still need you, and disappears when the count reaches zero.
See [the badge on the tray page](tray.md#attention-badge).

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