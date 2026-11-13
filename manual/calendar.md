# Calendar and meetings

Three tools work with your calendar: [`list_events`](tools.md#list_events),
[`find_free_times`](tools.md#find_free_times) and
[`send_meeting_invite`](tools.md#send_meeting_invite). They read Outlook live, so
Outlook must be running.

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
   `send_meeting_invite`.

Things to know:

- People whose free/busy can't be read, such as people outside your Exchange
  organization, are listed as unknown and ignored. Check with them yourself.
- Working hours, business-days-only, the time-zone label and whether tentative
  counts as busy come from [Settings, Scheduling](tray.md#scheduling). Claude can
  override the hours and business days per call.
- Suggestions are whole free blocks, in local time.

## Meeting invites

`send_meeting_invite` is the **only** tool that can send anything. Its behavior
depends on the mode you choose in Settings, Scheduling.

| Mode | What happens |
|---|---|
| **Send** (default) | A confirmation dialog opens. The invite goes out only if you click **Send** |
| **Draft only** | The meeting is saved in your calendar, unsent. You send it from Outlook |
| **Disabled** | The tool refuses. Claude is told to ask you to enable it |

Before anything is created, every recipient must resolve in Outlook. If one
doesn't, nothing is created.

### The confirmation dialog

In **Send** mode a small window opens on top of your other windows without taking
keyboard focus, and the tray icon turns amber. It shows:

- who organizes the meeting (you, or a shared mailbox)
- the subject, time, location and whether a Teams meeting was requested
- the full recipient list, with people **outside your organization highlighted**
- the invite body

You have three buttons:

- **Send** sends the invite at once.
- **Save as draft** saves the meeting in your calendar, unsent.
- **Cancel** creates nothing. It is the default button and `Esc` triggers it, so
  pressing Enter never sends.

A countdown shows the time left (default 120 seconds; change it in Settings). If
it runs out, the meeting is **saved as a draft**, not sent. Closing the window with
the X counts as Cancel. Requests queue up, and only one dialog is shown at a time.

Every outcome appears in bold in the [Activity window](tray.md#activity-window).

### Teams links

Clippy can't add a Teams link. If Claude asks for one, the meeting is created
without it and the result says so. Open the meeting in Outlook and click **Teams
Meeting**.

### Shared mailboxes

With `mailbox`, the meeting is organized from that shared mailbox's calendar, and
free/busy checks include that mailbox instead of yours.
