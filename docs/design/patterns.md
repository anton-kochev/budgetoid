# Patterns

How components compose into the product's recurring situations. These patterns are the
product's promises — seconds-fast entry, plain answers, an honest record — kept in
layout form.

## Money display

The rules for every monetary figure, everywhere:

- **Format with `Intl.NumberFormat`** (`style: 'currency'`) in the user's locale;
  never hand-assemble symbol + digits. Minor units follow the currency (2 for EUR,
  0 for JPY).
- **Expenses are unsigned ink.** Spending is the normal case; a list of expenses in
  ink reads as a record, not a rebuke. The domain stores negative amounts; the UI
  drops the sign and keeps the color neutral.
- **Income takes `+`** and `--bud-positive-text`. It is the marked case.
- **Over-budget figures** use `--bud-over` and words, not bare negatives: "Over by
  8.20 €". A minus sign is an accountant's answer; "over by" is a plain one.
- **Converted values are approximations**: muted, prefixed `≈`, never the primary
  figure, never bold. The real amount in the account's own currency always leads.
- Cents always shown, same size ([typography](typography.md)). Columns right-aligned,
  tabular.
- Screen-reader form: the accessible name spells it out — "8 euros 20 left in
  Groceries"; `≈` reads as "approximately" ([accessibility](accessibility.md)).

## The entry flow

Adding a transaction is the flagship interaction; its budget is seconds, end to end.
It opens from the Add button as a sheet (compact) or dialog (expanded).

- **Amount first.** The amount field is focused on open, numeric keypad up. An
  expense/income toggle sits beside it, defaulting to expense.
- **Everything else is pre-answered**: date pre-filled with today, account remembered
  from last time, payee completes from the counterparties this browser could open,
  ordered by name and narrowed by what has been typed (ordering them by recency is
  work, and unreachable from any read the product has: a payee carries an id and a
  sealed name and no timestamp, and *recently used* would need transaction history
  besides), category suggests the payee's last-used category. Category and payee stay
  optional — never block the record on classification.
- Field order: amount → payee → category → account → date → note. Submit enables once
  amount and account are valid.
- **An empty amount is not a zero.** An untouched amount field blocks submit; it never
  submits as zero. Zero is a legal amount — a purchase a voucher covered in full is a
  record worth keeping — so nothing beneath this form can tell a deliberate zero from a
  field nobody typed in. The form is the only place that knows the difference.
- **Save confirms and resets**: snackbar "Recorded." with Undo, form clears back to a
  focused amount field — repeat entry (several receipts in a row) never touches
  navigation.
- Errors surface on submit, on the field, in words ([components](components.md)); the
  keypad is never interrupted mid-entry.

## Empty states and first run

Every screen ships its empty state in the same commit as the screen; "no data" is a
state, not an error.

Copy structure (see [voice](voice.md)): one plain statement of fact, an optional line
of orientation, one action.

- Accounts — "No accounts yet." / "Every account in one place — one picture of your
  money." / **Add account**.
- Transactions — "Nothing recorded yet." / "Recording takes seconds — amounts, from
  either direction." / **Add transaction**.
- Categories — "No categories yet." / "Categories say what money was for." /
  **Add category**.
- Home, before anything exists, leads the first-run chain: welcome line → **Add your
  first account** → entry unlocks. First run is a sequence of these empty states, not
  a tour, not tooltips, not a checklist overlay.

The bead may breathe once on an empty state ([motion](motion.md)).

**Registration is not first run, and the two take opposite shapes on purpose.** First run is what
somebody who already has an account meets inside the app: nothing is required of them, they may do
the four things in any order or none, and the screens they land on are ordinary screens with nothing
in them yet — which is why the rule above forbids a checklist. Registration is the opposite animal.
It is one **ordered, atomic transaction**: three things happen in one sequence, none of them is
optional, and until the last press nothing exists. So it is a **linear sequence of full screens**,
one step at a time, each with a plain **`Step 2 of 3`** caption above its own heading — the caption
belongs to the step, per [components](components.md). Not a checklist, which would offer an order
that is not available, and not `MatStepper`, which draws a header a person can navigate and implies
they may go back to a step whose state is gone.

## Budget-state feedback

The three states from [color](color.md), applied with plain words and calm thresholds:

| State | Trigger | Meter | Words |
| --- | --- | --- | --- |
| On track | remaining > 20% of assigned | mint fill | "142.10 € left" |
| Near limit | remaining ≤ 20% | caution fill | "Getting close — 12.40 € left" |
| Over | remaining < 0 | over fill at 100% | "Over by 8.20 €" |

State changes color and words together, never color alone. No banners, no push alarm
for near-limit — the meter and its words are the answer to "can I afford this?", read
when the person asks.

## Theme

Both themes are first-class. Default follows the OS (`color-scheme`). `ThemeService`
already resolves, applies and persists a chosen mode to `localStorage['budgetoid-theme']`,
and `public/theme-prepaint.js`, loaded from `index.html`, applies it before first
paint — but **no UI calls it**: the Settings screen carries no theme control, so the
override exists in code and nowhere on screen.
Every new surface is designed and reviewed in both themes before shipping.

## Nothing to consent to

The product shall present **no consent banner, no cookie notice and no tracking-preference
surface**. It has nothing to ask consent for: it loads nothing from another origin except the three
trips to the identity provider a person starts — creating an account on `/register`, changing its
address on `/app/settings`, and releasing an account on `/release` — sets no cookie but the session
handle, and keeps
nothing on the device that is not strictly necessary for something the person asked for — never a
trail of what they did —
[no third-party origins](../engineering/no-third-party-origins.md#no-cookie-from-a-script-and-nothing-to-consent-to)
argues it. A consent surface over none of that would ask a question with only one true answer, and
teach the person to dismiss the next one unread.

If the product ever needs a new kind of data, it shall ask **at the point of use**, in plain words,
beside the control that needs it — never up front, never in a banner.

## Data ownership

The product publicly promises: the user only and always owns their data.

- **Export** (full, machine-readable) and **Erase** (complete) live together in
  Settings, one screen deep, always — never buried, never gated on contact/support.
- Erase is the one place the UI is deliberately slow: a dialog states plainly what
  will be deleted, requires typing a confirmation word, asks for a passkey the server
  checks, and its commit button is the Destructive variant. Export sits adjacent as the
  offered alternative. [components](components.md) specifies the dialog.
- Deleting lesser things (an account with transactions, a category in use) follows the
  same shape at smaller scale: state the consequence in plain words, then confirm.
  Blocked deletes (domain guards) explain what to do instead, and "instead" is the
  smallest act that clears the block — remove the transactions holding the account, not
  everything the user owns: "This account has transactions. Delete them first."

**Today's Settings screen** is `/app/settings`, and it is a destination: the shell draws it in
the bottom bar and the rail beside Transactions, Accounts and Categories, per
[components](components.md). It renders the account's
email address, a working Export that opens every name and note in the tab and saves a file a
person can read — whole or not at all, and only while this tab holds the account's keys — a
working **Sign out**, and a working **Erase everything**, which opens the confirmation dialog the
first bullet above describes. It states in plain words what the operator can read, and that erased
rows survive in point-in-time backups for up to seven days — once, on the screen, and not again in
the dialog.

It also lists **every way of signing in** — each entry its type in words and the day behind
it, and nothing more. A recovery-code set is one of those entries, because redeeming a code
opens a full session the way the other kinds do. Registering and revoking are present and
**disabled**, and the section carries two sentences rather than one. **Every inert control on the
screen carries a sentence of its own, naming the piece that control is missing** — and the rule is
stated without a count, because each control that ships retires one sentence and a tally written
here goes false the day it does. Registering a passkey says what holds it off in words of its own.
Revoking waits on a confirmation step it does not have yet, because it cannot be undone. Pasting any sentence over another puts one on the screen that is true of a different
control. **Export is held to the same rule on a control that is not inert**: it is off only while
this tab cannot open what the file is written from — a key rotation in flight, or a locked account
— and says which in a sentence of its own, or says nothing while an unlock is running.

**What no sentence may say any more is that this screen cannot ask for a passkey the server
checks.** Key rotation asks for exactly that one, a few sections down, and Erase everything asks
for it too. A sentence that names a checked passkey as the missing piece is therefore false in
front of anybody who has rotated their keys, and the Revoke and Generate sentences were rewritten
for that reason. Each sentence sits above the list rather than beside each row, so a screen reader
hears it once instead of once per entry. An entry nothing can ever revoke carries no button at all,
not even a disabled one.

Below that list sits **Recovery codes**, which says how many are left and
nothing more — no code, no part of one, no identifier, no date. It reads and never writes:
its Generate control is present and **disabled**, with a sentence of its own naming the two
surfaces it waits on — a confirmation, since replacing a set cannot be undone, and a place to show
the new codes once. Generating a set **from here** and redeeming one are unbuilt. Showing a set
once is built and lives elsewhere — the last step of registration, where the account's first set is
issued.

The Account section at the top of the screen carries **Change email address**, which is built: a trip to
Google's account chooser and back, then **Confirm with your passkey** in its place, with the result
stated in the section's one region. [components](components.md) owns its specification.

Below Recovery codes sits **Account keys**, and it is **built**: one Outline
**Unlock** running a passkey ceremony the browser mints and throws away, so that a person whose tab
reloaded gets their keys back without leaving the account. It is drawn only while there is
something to unlock, it carries no sentence saying what it waits on because it waits on nothing,
and [components](components.md) owns the rest of its specification.

Below Account keys sits **Key rotation**, and it is **built**: two blocks of prose, an
acknowledgement, one Destructive control — the first in the book to take that fill without deleting
anything — a polite region carrying the phase sentence and eleven refusals, and a determinate bar
beside it rather than inside it. The control is **Rotate keys**, or **Finish rotating** over a run
this account staged and never finished, and never both.
[components](components.md) holds the chapter and the four places what ships departs from it. The
one decision in it that reaches other screens is still unbuilt: while a rotation runs, the three
content screens are to render the locked-account notice in place of their lists, and today they
render their lists.

**Sign out is no longer the only exit from a locked account.** Count the producers of a
key-encryption key in this client and there are **four** — the assertion on `/welcome`, the
registration flow, this Unlock, and the rotation flow below it. The first two sit behind the guard
that turns an authenticated visitor away, so before the Account keys section shipped the only route
back to your own keys was to leave the account and come back in through one of them. That exit is
still on the screen, several sections up, and it is now one of three: a finished rotation hands the
promoted generation to custody on its way out, which unlocks the tab. **No copy anywhere says so**,
because somebody who wants to read their records should press the control that takes a second.

Nothing between the two is by accident: Export and Erase are a pair, so nothing goes between *them*
and everything else arrives above. The bullets above stay as written because they are the target,
not a description of what shipped.
