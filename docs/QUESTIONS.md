# Open questions for the user

The loop adds a question here instead of guessing when a choice changes
user-visible behavior, security, or a hard rule. Answer inline and remove
the `[!]` marker in `docs/PLAN.md`.

Format:

```
## Q<n> (task M#.#): <question>
Options: A) … B) …
Recommendation: …
Answer:
```

## Q1 (task M12, not blocking): How much should auto-update trust a GitHub release?
Auto-install (which you asked for) installs whatever the repository publishes as its latest full release, checked only by SHA-256 from the same release (integrity, not authenticity). A hijacked account, token or workflow could push an exe that every installed copy installs within a week without a prompt. See SPEC §20.5 and §18 open question 12.
Options: A) Ship as specified: `Updates = Install` by default, the risk stated on the manual's Security page, `Notify` recommended for cautious users. B) Additionally sign `SHA256SUMS.txt` with an offline Ed25519 key and build the public key into the app (a later milestone). C) Authenticode-sign the exe and require the same signer.
Recommendation: A now (M12 proceeds on it), B as a follow-up milestone.
Answer:

## Q2 (task M12, not blocking): CLAUDE.md hard rule for the updater
No existing hard rule is weakened. The proposed addition is in SPEC §18 open question 15 ("Self-update only from our own releases…"). The architect doesn't edit CLAUDE.md without your approval.
Options: A) Add the bullet as proposed. B) Leave CLAUDE.md as is; the spec and the safety scans cover it.
Recommendation: A.
Answer:

## Q3 (task M13.4, not blocking): Whose calendar for meeting times in shared-mailbox AI drafts?
When Clippy drafts a reply in a shared mailbox and the email asks for a meeting, it needs one "owner" calendar for free/busy and the ranking. See SPEC §21.6 and §18 open question 16.
Options: A) Your own calendar (you are the one who attends). B) The shared mailbox's calendar (like find_free_times with mailbox). C) Both must be free.
Recommendation: A (M13 proceeds on it).
Answer:

## Q4 (task M13.4, not blocking): How soon can the first offered time be in an AI draft?
Drafts are reviewed and sent later, so very near times may be gone by then. See SPEC §21.6 and §18 open question 17.
Options: A) Not before the start of the next local day. B) Not before a few hours from now (same-day options possible).
Recommendation: A (M13 proceeds on it).
Answer:

## Q5 (task M15.3, not blocking): Where should the "draft invite saved" note go?
With reply_to_id, Clippy leaves a note in Drafts. If an AI reply draft for the same email already exists, there would be two reply drafts. See SPEC §22.4 and §18 open question 18.
Options: A) Always a separate reply draft with a highlighted note line. B) Put the note at the top of the existing Clippy draft for that email when there is one, else a new draft.
Recommendation: A (simpler; M15 proceeds on it).
Answer: