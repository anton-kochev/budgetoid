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
- Contractions are welcome ("It’s ready."); apostrophes are typographic (’). **A quoted
  string that ships is transcribed and not typeset**, which is why the quotations below do
  not all spell the apostrophe the same way: the convention, and the reason it runs one
  direction only, is stated once in the [components](components.md) preamble.
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
- **Confirmation**: past-tense fact, one word if possible: "Recorded." "Exported." Where the
  result may already have been true before the press — the same value chosen again, or an earlier
  request that committed without its answer arriving — state the result instead of the act: "Your
  email address is &lt;address&gt;." A past-tense act claims a change that may not have happened.
- **Destructive confirm**: consequence in plain words, then the action as the verb:
  "This erases your account and everything in it — every budget, account, category,
  payee and transaction. There is no undo." → "Erase everything".
- **Blocked action**: the fact, then the way forward, and the way forward is the
  smallest act that clears the block: "This account has transactions. Delete them
  first."
- **Errors**: what happened + what to do: "Couldn't save — you're offline. It will
  retry." Never blame the person; the subject of an error sentence is the system.
- **Not built yet**: name the missing piece and what it waits on, in the same breath as
  the control it disables: "Revoking a passkey can’t be undone, so it needs a confirmation
  step of its own, and this screen doesn’t have one yet. Those buttons stay off until it
  does." A disabled control with no sentence beside it reads as a bug, and the person
  cannot tell a limitation from a failure.
- **Name the piece that is actually missing, and re-check it every time a capability
  lands.** The settings screen's sentences once said Budgetoid couldn't register passkeys,
  and went on saying it after the browser started registering them — a screen telling a
  person it cannot do what it did on the way in. They then said the screen asks for no
  passkey, which its own Unlock control made false, and narrowed to a passkey *Budgetoid
  checks itself* — which Key rotation made false in turn, by asking for exactly that
  passkey two sections away. What those sentences name now is what a person would meet if
  the control worked: a confirmation step, a place to show new codes. Each correction is
  narrower than the one before, which is the shape this rule produces when it is applied
  rather than admired.
- **Leaving for another site**: where the person goes, what they do there, where they come back,
  and what the trip costs here — in that order, as standing prose before the press. "Changing your
  email address takes you to Google to choose the account whose address you want, then brings you
  back here to confirm with a passkey." Name the site; no interstitial announcing that the person
  is leaving, and no label above the sentence. The return is a promise, so name only a place the
  flow actually lands on. The cost is a fact about this tab, stated in its own block, never as a
  warning: "Coming back reloads this page."
- **Controls blocked by different things get different sentences, and the rule carries no
  count.** Each inert control says what holds *it* off, and a control that is off only for a
  while — Export, while this tab cannot open what the file is written from — says so in its
  own sentence, or in none while an unlock is running. A tally of sentences or reasons written
  beside the rule is a claim the next shipped control makes false, so state the rule and let
  the screen be the count. One sentence pasted across several replaces an old falsehood with a
  new one, and reads as an apology nobody wrote for this control.
- **Controls blocked by the same thing say so, each in its own sentence.** Each sentence names its
  own control and its real reason, and where one reason holds several controls — the browser runs
  one passkey check at a time — they share it. Never invent a difference: it makes one sentence
  false.

## A sentence the API sends

Copy is governed by where it lands, not by where it was typed. A string the API sends for a
person to read is UI copy and answers to this chapter — including the ones authored in C#,
in a project nobody opens this book to work in.

The case that makes it concrete is a refused write. A message keyed to a field renders **the
server's sentence, verbatim**, beneath the control it names, per
[components](components.md): the `errors` map is the server's and the form is the client's,
so client-authored copy would need a lookup total over every key the server can send, and its
answer for a key nobody anticipated is a generic sentence standing at the one place a person
is trying to make a correction. Rendering what arrived is total by construction. What follows
from that is a sentence somebody reads under a field having been written in a
`ValidationException`.

- **The shape owed is the shape above.** *What happened plus what to do*, with the system as
  the subject. "Payee name must be unique." and "Category group name must be unique." carry
  the first half only: each states a rule and leaves the reader to work out that the way
  forward is to choose another name.
- **A sentence missing its second half is fixed where the sentence is.** Writing the better
  one in the browser is how a second definition of one rule is born — the API narrows the
  rule or adds one, the response is a 400 either way, and the client goes on rendering copy
  for the rule that did not fire, telling somebody to do something that will not work with
  nothing red on either side. The edit belongs in the API.
- **The rest of this chapter applies unchanged.** Sentence case, no exclamation marks, plain
  words, the nouns in the terminology table and the banned words with them: a server sentence
  naming a *merchant* or a *balance remaining* is as wrong as a template doing it.
- **It reaches the sentence and stops at the wire.** A conflict's `Detail` is copy — it is
  the whole of what a person is told, and this book revises it for readability. That is the
  reason nothing branches on it: a client reads the `conflictKind` member beside it and never
  the prose, so an edit made for a reader cannot reissue a contract. See
  [payees.md](../business-logic/payees.md). Where the client writes its own sentence for an
  outcome instead — the conflict copy in [components](components.md) — that sentence is this
  book's in the ordinary way.

## A secret shown once

The recovery-code hand-off is the only screen that shows a secret, and it has to do three
things no other screen does: say where the value came from, say that it will not be shown
again, and state a loss nobody can reverse — without any of it reading as alarm.

- **Say who made it and who never sees it, in that order.** "Your browser made these ten
  codes. Budgetoid never receives one, and this is the only time they’re shown." The
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
- **The acknowledgement is what the person did, not what they promise.** "I’ve saved
  these codes somewhere I can get to them." — past tense, about an act. "I understand the
  risk" asks for a feeling, which is not checkable and not what is wanted.
- **Say what is not yet true, on every step.** "Nothing is saved until the last step."
  removes the reason to be afraid of leaving, so no dialog has to ask.

## A long act that cannot be undone

A key rotation is the first thing the product does that is destructive, slow, and interruptible all
at once, and each of those three pulls the copy in a different direction. The chapter specifying it
is [components](components.md); what belongs here is the four rules its sentences answer to, because
the next act of this shape will reach for them.

- **Say what the act does, never how long it takes.** "This may take a few minutes." is the
  sentence to refuse: nothing measures it, a figure taken off one machine does not transport, and a
  person told minutes and given ten has been lied to by their own interface. A determinate bar is
  the honest version of that sentence, and a phase word beside it says more than a percentage does.
- **State the consequence as a fact, in its own block.** "This rewrites every record in the account,
  and nothing can put the old keys back." No label above it, no icon standing in for one — the
  secret-shown-once rule, for the same reason: a label tells the reader to brace instead of to read.
- **Say what an interruption costs, before it can happen.** "If this tab closes part-way through,
  the rotation stops where it is and this section offers to finish it — a rotation picked up again
  starts over from the first record." A progress bar that restarts looks broken to anybody who was
  not told, and this is the sentence that makes it read as honest instead.
- **An acknowledgement may name a future act when there is no past one to name.** "I’ll leave this
  tab open until it finishes." departs from the rule above it knowingly: nothing precedes a rotation
  the way saving the codes precedes leaving the hand-off, so the label names the one act that is the
  person’s to perform while it runs, and what it asserts is true at the moment it is ticked. "I
  understand this can’t be undone" is still refused — it asks for a feeling.
- **Every standing sentence stays true while the act is running and after it has finished.** Copy
  written as what the act *does*, rather than as what is about to happen, survives all three states;
  copy written for the resting state has to be swapped out mid-run, and the swap is what nobody
  maintains.

## An account nobody can open

The release screen is for somebody who has lost every passkey and every recovery code, and it is
the one screen where the worst news in the product is already true when the person arrives. The
chapter specifying it is [components](components.md), *Releasing an account*; these are the rules
its sentences answer to.

- **State the loss as already true, never as the button's doing.** "It’s already unrecoverable:
  nothing you hold can open it, and Budgetoid keeps no copy." The loss happened when the last
  factor went, and erasing neither causes it nor can undo it. "Erasing permanently deletes your
  data" puts the loss on the button, and a person reading it goes looking for a way to keep what
  they think they still have.
- **Say what the act frees, and that it recovers nothing, in the same sentence.** "Erasing the
  account recovers nothing — it releases the account and its email address, so you can create a
  new account with that address." *Release* is the verb because it is the honest one: the account
  and the address are what change hands, and nothing comes back with them.
- **Put the operator in the same position, out loud.** "— by you or by us." It is *A secret shown
  once*'s *to you, and to us*, met from the other side: there it is a warning, here it is the fact
  the warning was about.
- **Name only doors the screen has, and only ones this reader can open.** No recovery code offered
  as a way in, no support address, and no word about taking the schedule back: the one way to do
  that takes a passkey, and the person here holds none. A person this far down will try every door
  a sentence names, so a door that is not there — or not theirs — costs them more than silence
  would.
- **The acknowledgement names the loss as something that happened.** "I’ve lost every passkey and
  every recovery code for this account." Past tense, about an act — the rule above, applied to a
  loss rather than a save.
- **A date, not a countdown.** "This account will be erased on {date} at {time}." An absolute
  date in the reader's zone, per the mechanics above; *in 7 days* is false on the next day's load.
- **The locked account's words stay off this screen.** *Unlock*, *locked* and *this tab can’t read*
  each promise a press on this device opens something, and nothing this person holds does.

## A notice nobody asked for

The scheduled-erasure notice is the one sentence the product puts on every signed-in screen without
being asked, about an act the reader may not have made. The chapter specifying it is
[components](components.md), *Scheduled erasure notice*; these are the rules its sentence answers
to, in the order it says them.

- **The date first, absolute.** "This account will be erased on {date} at {time}." The reader's
  zone, per the mechanics above, and never a countdown.
- **What asked, as it was observed.** "A sign-in with its Google account asked for this." The
  product saw a credential, not a person, so it names the credential. *You asked* is false for the
  reader the notice is for, and *someone asked* accuses on the same missing fact.
- **Who can act, both readings in one clause.** "If that wasn’t you, or you’ve changed your mind,"
  — the owner who did not ask and the owner who did take the same act, so the sentence names it
  once.
- **Where, and with what.** "cancel it in Settings with a passkey." The place by the label the
  navigation already shows, and the one thing the act takes, so nobody holding only a Google
  sign-in walks to a control that refuses them.
- **No label, no address, no alarm.** Not *Warning*; no email address on a screen anybody at the
  device can see; and no guess at a cause the product did not observe. It is standing prose, read
  in reading order, and not an announcement: a sentence that interrupts every screen is a sentence
  a reader learns to skip.

## Marketing voice (Welcome and public surfaces)

Currency-free, no feature lists, no trust-claim lists, no gimmick lines. The shipped
Welcome copy is the reference: "Always watching. Never judging." — statements, then the
acts. Two of those are offered and exactly one of them is Primary, per
[components](components.md); the voice rule is that the screen sells one thing and the
second control is there to be found rather than to persuade. Anything that reads as a
sales trick gets cut. **A third element sits below the two, and it is a question, not an act**:
"Lost every passkey and recovery code?" It is phrased as the question only the person it is for
answers yes to, so it persuades nobody and sells nothing, and it goes to a screen that says plainly
what it cannot do.
