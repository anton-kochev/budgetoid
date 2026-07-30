# Privacy, Data Ownership, and Anonymity

A budget is a diary written in money. Transaction descriptions and payee names record where
a person was, what they cared about, what they struggled with — a narrative more revealing
than most private correspondence. A product that asks for that record owes its user more
than a checkbox promise.

**The user only and always owns their data.** Budgetoid holds the minimum it needs to do
its job, protects what it holds at every layer it controls, and can hand everything back —
or destroy everything — on demand. Privacy is a product feature, held to the same standard
as any other, not a compliance exercise.

## What the product stores, and why

- **Sign-in identity.** The subject identifier from the user's sign-in provider (the key
  the account hangs on), an email address, and a display name. The email and name exist
  for support and greeting, not for the budget to work — the direction of travel is to
  need them not at all (see *Minimization* below).
- **The budget itself.** Budgets, accounts, category groups, categories, payees, and
  transactions. Amounts, dates, and category links are the arithmetic of budgeting; the
  free-text fields — transaction descriptions and payee names — are the sensitive part,
  because they are the narrative.
- **Nothing else.** No analytics events, no behavioral tracking, no advertising
  identifiers, no third-party scripts in the app. The server does not record who did what
  when beyond what serving the request requires — an audit trail of user behavior would
  itself be a privacy liability, so the product deliberately does not keep one.

## How data is protected

These guarantees are shipped and enforced by the lowest layer able to enforce them:

- **Isolation is enforced by the database, not by good intentions.** Every budget-owned
  row is guarded by PostgreSQL row-level security; a connection serving one budget cannot
  read or write another budget's rows, whatever code produced the query
  ([ADR 0005](../decisions/0005-isolate-budget-owned-rows-with-row-level-security.md)).
- **The application holds least privilege.** The API connects as a role that can only do
  what the domain allows — immutable columns are not grantable, and a bypassed rule fails
  loudly rather than succeeding silently
  ([ADR 0004](../decisions/0004-connect-as-a-least-privilege-role.md)).
- **There is no database password to steal.** Production authenticates to PostgreSQL with
  a managed identity
  ([ADR 0007](../decisions/0007-authenticate-to-postgres-with-managed-identity.md)), and
  database traffic never crosses the public internet
  ([ADR 0009](../decisions/0009-route-database-traffic-over-a-private-endpoint.md)).
- **Deleted means deleted.** The schema keeps no soft-delete flags, no tombstones, no
  restore path — a deleted row is gone, by design, because the product promises complete
  erasure and a hidden copy would make that promise a lie.

## Export

The user can take everything home in one click: a single machine-readable JSON document
containing the user record, every budget, account, category group, category, payee, and
transaction. Nothing is summarized, sampled, or held back — the export is the whole truth
the server knows. It stays one click away, never buried behind support tickets or waiting
periods.

## Erasure

One action deletes the account and everything under it — every budget, account, category,
payee, and transaction — permanently and in a single transaction, so there is no
half-deleted state. The promise is honest about its one physical limit: erased data
persists inside point-in-time backups until the retention period expires, and then it is
gone everywhere. No copy survives that window, because no other copy exists.

## Minimization

The product asks for data at the moment a feature needs it, and not before. Email and
display name are stored today only because sign-in hands them over; they will become
optional and eventually unnecessary. What is never collected never leaks, never needs
protecting, and never has to be erased — minimization is the cheapest privacy guarantee
there is, so it wins whenever a field's purpose cannot be named.

## Anonymity

Protection and minimization still leave two parties who know too much: the sign-in
provider, which learns that this person uses a budgeting app, and the server, which can
read the diary. Both are removable, and removing them is the destination:

- **Sign-in that identifies the user to no one.** A passkey is an anonymous credential —
  no email, no account at a third party, no identity provider observing each sign-in. An
  account keyed on a passkey belongs to a person no system component can name. The honest
  trade-off: recovery becomes the user's responsibility, mitigated by registering multiple
  passkeys and by recovery codes.
- **Content the server cannot read.** The narrative fields — transaction descriptions and
  payee names — become encrypted on the user's device, with keys derived from the passkey
  itself (the WebAuthn PRF extension), so unlocking the app and unlocking the data are the
  same gesture and no master password exists to forget. Amounts, dates, and category links
  stay server-readable, because they are what budgeting math, filters, and reports run on.
  The server then holds numbers without a story: a breach, an insider, or a legal demand
  can surface how much, but never what for or with whom.

## What the product will never do

- Sell, share, or mine the user's data. There is no business model in which the budget is
  the product.
- Track behavior — no analytics identifiers, no session recording, no audit log of the
  user's actions.
- Ask for consent theater. There are no third-party cookies and no trackers, so there is
  no banner; if the product ever needs a new kind of data, it asks at the point of use,
  plainly.
