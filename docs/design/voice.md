# Voice

The interface speaks the way the product thinks: plain answers, never judgment. Every
string is a fact or a next step, in words a person uses at a kitchen table.

## Tone

- **Plain.** "12.40 € left" — not "Remaining allocation balance". If a sentence needs
  a financial glossary, rewrite it.
- **Never judging.** The system never says money was spent badly. Not "You overspent",
  but "Over by 8.20 €". Not "Warning", but what is true and what to do.
- **Calm.** No exclamation marks. No urgency theater ("Act now!"), no praise theater
  ("Great job!"). The reward for recording a transaction is the record.
- **Brief.** Buttons are verbs ("Add account", "Record", "Export"). Titles are nouns.
  Confirmations are short facts ("Recorded.").
- **Honest.** Converted amounts say `≈`. Estimates say so. Errors say what happened
  and what to do, not "Something went wrong" when we know what went wrong.

## Mechanics

- Sentence case everywhere — titles, buttons, labels. Never Title Case, never ALL CAPS
  (the uppercase eyebrow is a type style, not a writing style).
- Money is always numerals, never words, formatted per [patterns](patterns.md).
- Dates in the user's locale; relative words ("Today", "Yesterday") only in lists,
  absolute dates everywhere else.
- Contractions are welcome ("It's ready."); apostrophes are typographic (').
- The product name is lowercase **budgetoid** only in the lockup; in prose it is
  Budgetoid.

## Terminology

| Canonical | Meaning | Never say |
| --- | --- | --- |
| **record** (verb) / **transaction** (noun) | Writing a money movement down | log, post, book |
| **available** / **left** | What a purpose can still spend | balance remaining, unspent allocation |
| **assigned** | Money given to a purpose | allocated, budgeted (as a verb) |
| **purpose** | What a sum of money is for — the envelope concept, when it ships | job (in UI), envelope, bucket, pot |
| **category** / **category group** | Classification of a transaction (today's domain) | folder, tag |
| **account** | A place money lives | wallet, source |
| **payee** | Who money went to or came from | merchant, vendor, counterparty |
| **over by X** | The over-budget fact | overspent, in the red, negative balance |
| **getting close** | The near-limit fact | warning, danger, low funds |

**Why "purpose" and not "job":** the credo — *money gets its jobs first* — keeps
"jobs"; it is the brand's metaphor and stays in marketing voice (the Welcome screen's
"All of it gets a job."). Inside the product, "job" collides with employment in the
one app that is entirely about money, so screens use **purpose**, which is also the
mission's own word ("decide what your money is for"). Today's screens keep the domain
nouns Category and Category group; "purpose" is reserved for the allocation layer when
it becomes first-class.

## Banned words

**ledger**, **buffer** — banned as product terms outright. Also avoid: sin/guilt
framing ("splurge", "guilty pleasure"), finance jargon ("debit", "credit" as UI
verbs), and hedge words that dodge a plain answer ("approximately" belongs to `≈`
figures, not to copy that could just say the number).

## Microcopy patterns

- **Empty state**: fact → orientation (optional) → action.
  "No accounts yet." / "Every account in one place — one picture of your money." /
  "Add account".
- **Confirmation**: past-tense fact, one word if possible: "Recorded." "Exported."
  "Erased."
- **Destructive confirm**: consequence in plain words, then the action as the verb:
  "This erases every account, transaction, and category. There is no undo." →
  "Erase everything".
- **Blocked action**: the fact, then the way forward, and the way forward is the
  smallest act that clears the block: "This account has transactions. Delete them
  first."
- **Errors**: what happened + what to do: "Couldn't save — you're offline. It will
  retry." Never blame the person; the subject of an error sentence is the system.
- **Not built yet**: name the missing piece and what it waits on, in the same breath as
  the control it disables: "Erasing has to be confirmed with a passkey Budgetoid checks
  itself, and this screen doesn't ask for one yet. The button stays off until it does."
  A disabled control
  with no sentence beside it reads as a bug, and the person cannot tell a limitation from
  a failure.
- **Name the piece that is actually missing, and re-check it every time a capability
  lands.** The sentence above once said Budgetoid couldn't register passkeys, and it went
  on saying it after the browser started registering them — a screen telling a person it
  cannot do what it did on the way in. It then said this screen asks for no passkey, which
  the Settings screen's own Unlock control made false in turn; the qualifier *Budgetoid
  checks itself* is what survives, because the assertion an unlock runs is minted in the
  browser and thrown away, and the one erasure waits on is verified by the server. Each
  correction is narrower than the one before, which is the shape this rule produces when it
  is applied rather than admired.
- **Controls blocked by different things get different sentences** — three of them on that
  screen today. One sentence pasted across several replaces an old falsehood with a new one,
  and reads as an apology nobody wrote for this control.

## A secret shown once

The recovery-code hand-off is the only screen that shows a secret, and it has to do three
things no other screen does: say where the value came from, say that it will not be shown
again, and state a loss nobody can reverse — without any of it reading as alarm.

- **Say who made it and who never sees it, in that order.** "Your browser made these ten
  codes. Budgetoid never receives one, and this is the only time they're shown." The
  provenance is the reassurance; the finality is the instruction.
- **State the loss as a fact about the system, not a threat to the reader.** "Budgetoid
  keeps no copy of either, so if you lose the passkey and every code, everything you
  record here stays locked — to you, and to us." The clause *to you, and to us* is doing
  the work: it says the operator is in the same position, which is the whole design and
  the only thing that makes the sentence honest rather than a disclaimer.
- **No label above it.** Not "Warning", not "Important", not an icon standing in for one.
  The sentence carries its own weight and a label tells the reader to brace instead of to
  read.
- **Name what a convenience costs, beside the convenience.** "Copying puts them on your
  clipboard, where other apps on this device can read them." Not a hidden footnote and
  not a confirmation dialog — a plain sentence next to the button, so the person chooses
  with the cost in view.
- **The acknowledgement is what the person did, not what they promise.** "I've saved
  these codes somewhere I can get to them." — past tense, about an act. "I understand the
  risk" asks for a feeling, which is not checkable and not what is wanted.
- **Say what is not yet true, on every step.** "Nothing is saved until the last step."
  removes the reason to be afraid of leaving, so no dialog has to ask.

## Marketing voice (Welcome and public surfaces)

Currency-free, no feature lists, no trust-claim lists, no gimmick lines. The shipped
Welcome copy is the reference: "Always watching. Never judging." — statements, then the
acts. Two of those are offered and exactly one of them is Primary, per
[components](components.md); the voice rule is that the screen sells one thing and the
second control is there to be found rather than to persuade. Anything that reads as a
sales trick gets cut.
