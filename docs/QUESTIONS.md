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