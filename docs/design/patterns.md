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
  from last time, payee autocompletes from history (recent first), category suggests
  the payee's last-used category. Category and payee stay optional — never block the
  record on classification.
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

## Data ownership

The product publicly promises: the user only and always owns their data.

- **Export** (full, machine-readable) and **Erase** (complete) live together in
  Settings, one screen deep, always — never buried, never gated on contact/support.
- Erase is the one place the UI is deliberately slow: a dialog states plainly what
  will be deleted, requires typing a confirmation word, and its commit button is the
  Destructive variant. Export sits adjacent as the offered alternative. (The dialog and
  its confirmation word are not built; today's control is disabled — see below.)
- Deleting lesser things (an account with transactions, a category in use) follows the
  same shape at smaller scale: state the consequence in plain words, then confirm.
  Blocked deletes (domain guards) explain what to do instead, and "instead" is the
  smallest act that clears the block — remove the transactions holding the account, not
  everything the user owns: "This account has transactions. Delete them first."

**Today's Settings screen** is `/app/settings`. It has no entry in the bottom bar or the
rail and is reached by typing the URL — a later epic gives it one. It renders the account's
email address, a working Export that saves the server's response bytes unread, and an Erase
control that is present and **disabled**, because erasure has to be confirmed with a fresh
passkey assertion and nothing in the browser runs one. It states in plain words what the
operator can read, and that erased rows survive in point-in-time backups for up to seven days.

It also lists **every way of signing in** — each entry its type in words and the day behind
it, and nothing more. A recovery-code set is one of those entries, because redeeming a code
opens a full session the way the other kinds do. Registering and revoking are present and
**disabled**: no screen on this path runs the ceremony either needs — the client runs a
creation ceremony only inside registration, and an assertion nowhere at all. One sentence
above the list explains it, rather than one beside each row, so a screen reader hears it once
instead of once per entry. An entry nothing can ever revoke carries no button at all, not even
a disabled one.

Between that list and Export sits **Recovery codes**, which says how many are left and
nothing more — no code, no part of one, no identifier, no date. It reads and never writes:
its Generate control is present and **disabled** on the argument the others use, so generating
a set **from here** and redeeming one are unbuilt, as are key rotation, the email-change action
and the erasure confirmation dialog. Showing a set once is built and lives elsewhere — the last
step of registration, where the account's first set is issued. The section sits there and
nowhere else because Export and Erase are a pair and nothing goes between them. The bullets
above stay as written because they are the target, not a description of what shipped.
