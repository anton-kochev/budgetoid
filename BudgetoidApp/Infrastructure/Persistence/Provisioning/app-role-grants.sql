-- Provisions the least-privilege application role and its exact grant matrix. Idempotent by
-- design: the test hosts run it on every container, local dev runs it on every boot, and
-- production re-runs it on every deploy. Each table's block REVOKEs the role's privileges
-- before re-granting, so a re-run converges the role to exactly what is written here —
-- removing a line removes the privilege on the next run instead of leaving it behind.
--
-- PostgreSQL column privileges are ADDITIVE. REVOKE UPDATE (col) ON t cannot subtract a
-- column from a table-wide GRANT UPDATE ON t — it only removes a previously granted
-- column-level privilege. Immutability is therefore expressed by granting UPDATE with an
-- explicit column list and simply leaving the immutable columns off it. Do not "simplify" a
-- column-list grant into a table-wide one: that silently re-opens every immutability hole
-- the list exists to close.
--
-- Grants name every table explicitly, so a new table is invisible to the role until someone
-- grants it here. That is intended (fail-closed): when a new feature fails with 42501, add
-- the narrowest grant it needs to this file — never GRANT ALL.

-- The role is created WITHOUT a credential, and this file contains no secret of any kind. How
-- budgetoid_app proves who it is differs per environment and is attached separately: in
-- production a Microsoft Entra security label binds it to the API's managed identity
-- (DatabaseProvisioning.AttachAppRoleIdentityAsync), and locally a password is set
-- (AttachAppRolePasswordAsync). Keeping that out of here is what lets the same script run
-- unchanged against a container, a dev machine, and Azure — and it means a re-run can never
-- reset a credential the environment owns.
--
-- LOGIN is granted here because it is a property of the role's purpose rather than of its
-- credential: a role that cannot log in cannot serve a request under any authentication scheme.
-- Without a password and without a label, LOGIN alone still authenticates nothing.
DO $provision$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'budgetoid_app') THEN
        CREATE ROLE budgetoid_app WITH LOGIN;
    ELSE
        -- Converges an existing role on LOGIN without touching its credential.
        ALTER ROLE budgetoid_app WITH LOGIN;
    END IF;
END
$provision$;

-- USAGE only: the role can resolve objects in the schema but cannot CREATE new ones.
GRANT USAGE ON SCHEMA public TO budgetoid_app;

-- currencies: reference data seeded by the migration; the application only reads it.
REVOKE ALL ON currencies FROM budgetoid_app;
GRANT SELECT ON currencies TO budgetoid_app;

-- users: a user row carries no identity key of its own — sign-in resolves through credentials
-- below — so email, the address the account is reached at, is the only updatable column.
-- created_at_utc is an audit fact, immutable by omission from the list. A one-column list is
-- still a list: do not collapse it into a table-wide GRANT UPDATE ON users, which would take
-- created_at_utc with it.
--
-- DELETE is here so that erasing an account runs as this role rather than on an elevated
-- connection, which is the whole point of ADR 0004. It is policed: users carries user_isolation,
-- so the role can only delete the row the session names.
--
-- IT IS THE ONLY TABLE ERASURE NEEDS A NEW GRANT ON, and the six absent grants are a decision
-- rather than an oversight. Every owned table hangs off this row by ON DELETE CASCADE — users →
-- budgets → {payees, accounts, category_groups → categories}, and users → credentials →
-- {sessions, passkey_public_keys, passkey_signature_counters} — and PostgreSQL performs a
-- referential action through internal triggers that run with the privileges of the REFERENCING
-- table's owner, not of the role that issued the statement. So this one grant empties the
-- structural graph and a grant on any child buys nothing.
-- Database_AllowsDeletingAUserAndCascadesTheAccountAway is what says so: it deletes as this role
-- and asserts every child table is empty afterwards.
--
-- IT IS NOT SUFFICIENT ON ITS OWN, and reading it that way is the mistake this paragraph exists to
-- stop. Five edges in the owned graph are Restrict rather than Cascade, and transactions is the
-- child of four of them — transactions → budgets, → accounts, → categories and → payees. Those four
-- are the guard that stops an ordinary delete taking recorded money movement with it, and a
-- budgeting product's accounts hold transactions, so a delete that leant on the cascade would
-- answer 23503 in the ordinary case rather than the corner. Erasure therefore empties transactions
-- first and then deletes this row; emptying that one table also disarms the fifth edge,
-- categories → category_groups, because a Restrict edge cannot bite once its child rows are gone.
--
-- The transactions grant already exists further down. What the erasure feature adds is the
-- sequence, and it runs on ONE session rather than two: SessionContextInterceptor writes
-- app.current_user_id and app.current_budget_id in the same statement on every connection open, so
-- the connection serving an authenticated request already names both the user for user_isolation
-- and the budget for budget_isolation.
--
-- Two of the six would cost something to add. credentials and passkey_public_keys are exempt from
-- row-level security — they are read before the request has an identity a policy could key on — so
-- a DELETE there would be UNPOLICED, and one statement carrying the wrong id would remove somebody
-- else's only way in with nothing to catch it. The cascade reaches those same rows as the table
-- owner, which is scoped by the row it descends from rather than by a privilege. Do not "complete"
-- this set: adding a grant to a child widens the role's reach without extending what erasure can
-- do.
REVOKE ALL ON users FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON users TO budgetoid_app;
GRANT UPDATE (email) ON users TO budgetoid_app;

-- credentials: the identity columns — user_id, type, provider, subject, created_at_utc — are
-- immutable. Changing a subject would silently repoint an account at a different principal, and
-- changing user_id would move a sign-in between accounts; a credential's identity is written
-- whole at registration and has no edit that means anything.
--
-- That is every column, so there is no UPDATE grant of any shape rather than a column list with
-- nothing on it — and that is now a property of the table rather than a snapshot of it. The one
-- mutable value the credential story was expected to bring, a passkey's signature counter, did not
-- land here: it lives on passkey_signature_counters below, which carries user_id and is policed.
-- See docs/decisions/0012. Anything proposed for this table from here on has to answer the same
-- question that one did, and the answer is a table boundary rather than a column on this one.
--
-- DELETE, and this is the re-argument the paragraph that stood here asked for. Revoking a passkey
-- is the path that landed, and it removes the credential row rather than marking it: the pinned
-- column set below refuses a revoked_at_utc here, and a revoked-but-present credential is a row a
-- bug can bring back. The child tables need no grant of their own — the cascade reaches
-- passkey_public_keys, passkey_signature_counters and sessions as the table OWNER rather than as
-- this role.
--
-- What this grant is NOT bounded by is a policy. credentials is exempt from row-level security, so
-- unlike every other DELETE in this file its blast radius is the whole table and the application is
-- the only thing narrowing it. Three things carry that weight, and all three have to stay true:
-- the delete takes a LOADED ENTITY rather than an id, and every read that can produce one naming a
-- row of this table is owner-and-type-scoped (today that is exactly one, FindPasskeyCredentialAsync;
-- a Credential built by hand gets a fresh id from its factory, so it names no row here and its
-- delete matches nothing); credentials.user_id is immutable, so an id cannot change owner between
-- that read and the write; and the two share one transaction. Note the first leg is a REVIEW rule
-- over PasskeyRepository rather than something the type system holds — an unscoped query added there
-- returning a Credential widens this grant back to the whole table, and no test below the
-- application would say so. See docs/decisions/0014,
-- which also records why policing this table instead is not available — RLS is enabled per table
-- and not per command, so a FOR DELETE policy alone would refuse the discovery SELECT that the
-- exemption exists for.
--
-- Database_LetsTheAppRoleDeleteAnyCredential_OnASessionNamingNobody states the unbounded half as an
-- executable test, and Revocation_OfAnotherAccountsCredential_IsRefusedAndRemovesNeitherAccountsRows
-- is the only thing that would notice the application's scoping going. Erasure does not use this
-- grant and would still work without it: it empties this table through the cascade from users.
--
-- Note what a column added here would land on. The exemption was granted to one QUERY — the one
-- that discovers who is asking — but PostgreSQL applies it to the whole TABLE, so anything added
-- here is readable by every application session regardless of who that session names. That mismatch
-- is cheap while the columns are (id, user_id, type, provider, subject, created_at_utc) and stops
-- being cheap the moment a wrapped key or a recovery-code hash joins them. The recovery-code hash
-- has since stopped being hypothetical and landed on recovery_code_hashes instead, which is this
-- paragraph working rather than a reason to retire the example: the hypothetical is what made the
-- decision visible before there was anything to decide about, so it stays.
--
-- So the exemption pins that column set, and adding a column here goes red. The red means MOVE THE
-- COLUMN, not widen the pin: a WebAuthn assertion verifies its signature with the stored public key
-- before it knows whose account it is, so the discovery columns have to stay reachable with no
-- identity on the session — but everything read AFTER that answer belongs on a table carrying
-- user_id, which the coverage rule polices by itself. See docs/decisions/0011, which also records
-- the trap waiting for anyone who tries to fix this by policing credentials instead.
REVOKE ALL ON credentials FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON credentials TO budgetoid_app;

-- sessions: a session's identity — user_id, credential_id, kind, created_at_utc, expires_at_utc —
-- is written whole when the session is established and has no edit that means anything. Changing
-- credential_id would relabel which key opened the door, which is the fact revocation is decided
-- by; changing kind would hand budget content to a session a federated credential opened, and that
-- is the one thing the kind exists to refuse. revoked_at_utc is the only column an edit can
-- legitimately reach, and it is therefore the whole UPDATE list. A one-column list is still a list:
-- do not collapse it into a table-wide GRANT UPDATE ON sessions, which would take the five above
-- with it.
--
-- No DELETE, and that is a decision rather than an omission. Revocation writes revoked_at_utc
-- rather than removing the row, so the role holds no privilege that can make a session
-- unaccountable, and re-revoking converges instead of failing as a second delete of nothing. The
-- usual argument for DELETE — that updated rows accumulate — does not separate the two options: an
-- unrevoked but expired row accumulates identically, so retention is a problem either mechanism
-- has and neither solves. Sweeping expired and revoked rows is a path that does not exist yet;
-- when it lands it needs this grant, and this paragraph is what has to be re-argued rather than
-- quietly deleted.
--
-- Note what a session row is NOT: a tombstone. It exists only while its account does — the cascade
-- from credentials, and through it from users, takes every one of them — so a revoked session
-- leaves nothing behind an erasure.
REVOKE ALL ON sessions FROM budgetoid_app;
GRANT SELECT, INSERT ON sessions TO budgetoid_app;
GRANT UPDATE (revoked_at_utc) ON sessions TO budgetoid_app;

-- passkey_public_keys: everything on this table is read to decide whether the signature on an
-- assertion is genuine, which a WebAuthn ceremony has to answer BEFORE it knows whose account it is.
-- So it is exempt from row-level security for the same reason credentials is, and it carries the
-- same kind of pinned column set.
--
-- What holds this exemption to its reason is the PINNED COLUMN SET in RowLevelSecurityCoverage, not
-- the two grant lines below. Read that carefully, because the appealing answer is the wrong one. The
-- hazard is not mutation: the exemption is granted to a QUERY and applied to the whole TABLE, so
-- every column here is readable by every application session whoever that session names. A wrapped
-- key or a recovery-code hash is written once and never updated — each satisfies an append-only rule
-- perfectly, and this table, already holding key material, is the most attractive place in the schema
-- to propose one. The pin is what turns a new column red; the answer to that red is to MOVE THE
-- COLUMN to a table carrying user_id, never to widen the pin.
--
-- The grants are the corollary, and worth having because they are checkable in one line: no UPDATE of
-- any shape and no DELETE, ever, so mutable per-user state cannot accumulate here. credentials is
-- exempt on a table that could one day take an UPDATE column list; this one cannot. That is a real
-- narrowing — it just does not cover the case that actually threatens the exemption, which is a
-- secret that never changes. See docs/decisions/0012.
--
-- Rows leave only by the cascade from credentials, and through it from users, so a passkey leaves
-- nothing behind an erasure.
REVOKE ALL ON passkey_public_keys FROM budgetoid_app;
GRANT SELECT, INSERT ON passkey_public_keys TO budgetoid_app;

-- passkey_signature_counters: the counter an authenticator reports, compared to detect a cloned
-- one. It sits apart from the public key because the specification orders the ceremony that way —
-- the signature is verified first and the counter is compared only after, so by the time this table
-- is read the request has a trusted identity and can be policed like everything else. It carries
-- user_id, so the coverage classifier reaches that verdict from the columns without being told.
--
-- signature_counter is the only column an edit can legitimately reach, and it is therefore the whole
-- UPDATE list. credential_id, user_id and credential_type are immutable by omission. A one-column
-- list is still a list: do not collapse it into a table-wide GRANT UPDATE, which would take the
-- three above with it and let a bug repoint a counter at another account's credential.
--
-- No DELETE; rows leave by the cascade from credentials.
REVOKE ALL ON passkey_signature_counters FROM budgetoid_app;
GRANT SELECT, INSERT ON passkey_signature_counters TO budgetoid_app;
GRANT UPDATE (signature_counter) ON passkey_signature_counters TO budgetoid_app;

-- webauthn_challenges: the nonce each ceremony is bound to. It belongs to a ceremony rather than to
-- a person — one of the three, authentication, issues one before anybody has said who they are,
-- which is what a discoverable credential means — so for that pool there is nobody for a policy to
-- key on, and the table carries no user_id at all. The other two pools are issued to a signed-in
-- person and share the same shape deliberately: what binds a re-authentication nonce to an account
-- is the owner-scoped credential lookup in the handler that spends it, not a column here. Exempt
-- with that written reason, and its column set pinned, because the pin is what stops a
-- person-identifying column landing here later.
--
-- Of the seven identity tables — users, credentials, sessions, passkey_public_keys,
-- passkey_signature_counters, recovery_code_hashes and this one — exactly four are granted DELETE,
-- and each for a reason the other three do not have. This one, because these rows are nonces:
-- consuming one IS deleting it, which is the property that makes a challenge single-use, and a row
-- nobody can delete is a row swept by a path that does not exist. recovery_code_hashes, because that
-- same sentence is true of a recovery code word for word — see its block. credentials, because
-- revoking a passkey removes the row rather than marking it, scoped by the application and by
-- nothing beneath it — see ADR 0014. users, because it is the root every other owned row cascades
-- from, so deleting it is how an account is erased — see that block for why the cascade means the
-- three in between need no grant of their own. Contrast the sessions block, where revocation writes
-- a column precisely so the row stays accountable — opposite decisions, because the rows mean
-- opposite things.
--
-- (This paragraph said "two of six" before recovery_code_hashes existed, and it was already wrong
-- then: credentials had held DELETE since ADR 0014 and the count never moved with it. A count in
-- prose is a claim nothing executes, so it drifts silently — which is the argument for reading the
-- GRANT lines rather than trusting this sentence, and for fixing it when you notice.)
-- (The budget-owned tables further down hold DELETE too, for the ordinary reason that a person may
-- delete their own accounts, categories and transactions.)
--
-- Note the cost this accepts: the leg that issues an authentication challenge is unauthenticated, so
-- this is the one table any caller can make the role insert into. Growth is bounded by a short
-- lifetime and an opportunistic sweep of expired rows, not by rate limiting, which does not exist
-- here.
REVOKE ALL ON webauthn_challenges FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON webauthn_challenges TO budgetoid_app;

-- recovery_code_hashes: one row per recovery code that has not been redeemed, holding the SHA-256
-- of a verifier the client derives from the code. The code itself never reaches this deployment at
-- all, so nothing here can be turned back into one.
--
-- Exempt from row-level security, and it is the third table resting on the same argument credentials
-- and passkey_public_keys rest on: the row is found before the request has an identity a policy could
-- be keyed on. A recovery code is redeemed ANONYMOUSLY — somebody redeeming one has lost the
-- authenticator that would have proved who they are — so the lookup by hash is what establishes the
-- identity, and a policy keyed on app.current_user_id would refuse the very query that produces the
-- value it wants to compare against. It would refuse it loudly rather than quietly: an unset setting
-- reaches the policy as ''::uuid and raises 22P02 on every redemption. That is the trap ADR 0012
-- records, and this is the third table to walk up to it.
--
-- DELETE, and the sentence that earns it is the webauthn_challenges sentence word for word: these
-- rows are single-use secrets, so consuming one IS deleting it. FR-054 says a redeemed code is
-- invalidated, and a removed row is the only spelling of that which needs no second mechanism to be
-- believed — no used flag a bug can clear, no timestamp a reader has to remember to filter on, and
-- nothing left for the erasure remnant vocabulary to find. The remaining count is count(*).
--
-- No UPDATE of any shape, and that is the corollary worth having because it is checkable in one
-- statement: with no UPDATE the delete is the only way a row can stop counting, so the decision
-- above cannot be quietly reversed into a stamp. Database_RefusesEveryUpdateOnARecoveryCodeHash_...
-- is that statement, and Database_LetsTheAppRoleDeleteAnyRecoveryCodeHash_OnASessionNamingNobody
-- states the unbounded half — it goes red the day somebody polices this table.
--
-- The delete is unpoliced, like credentials' and unlike users'. ADR 0014's three legs are what hold
-- it, and all three have to keep holding: the delete takes the LOADED ENTITY and never a hash a
-- caller supplied, every read that produces one is either owner-and-type-scoped or IS the discovery
-- lookup this exemption exists for, and the read and the write share one transaction. What makes the
-- discovery-scoped delete sound rather than merely narrow is that the caller's own input names the
-- row: it is found by SHA-256 of a 256-bit secret they must present in full, so selecting a row you
-- cannot name is guessing it. That is not a new argument — ConsumeAsync already deletes a challenge
-- by the nonce the caller presents, on the table directly above.
--
-- Nothing uses this grant yet, and that is worth saying plainly rather than letting the paragraph
-- above read as a description of live traffic. Redemption is the one path that will, and it is not
-- routed; regenerating a set deletes the SET's credentials row instead, and these rows leave by the
-- cascade from it. So the role holds a privilege nothing exercises — the same shape as the
-- GRANT UPDATE (email) ON users above — and when redemption lands it becomes the single caller. A
-- reader looking for a second one will not find it, and should not add one.
--
-- Note what a column added here would land on, because it is the same mismatch as credentials': the
-- exemption is granted to one QUERY and applied by PostgreSQL to the whole TABLE. The pinned column
-- set in RowLevelSecurityCoverage is what holds it to its reason, and the columns it pins are what
-- the lookup needs before an identity exists. A wrapped key is the one this table will be offered
-- first — Story 12.1 wraps the account's keys under every recovery factor, and this is the obvious
-- place to put the recovery-code copy. It is the wrong place, for the reason the pin exists: a
-- wrapped key is read AFTER redemption has answered who is asking, so it belongs on a table carrying
-- user_id, which the coverage rule polices by itself with no argument needed. When the pin goes red
-- the fix is to MOVE THE COLUMN, never to widen the pin.
REVOKE ALL ON recovery_code_hashes FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON recovery_code_hashes TO budgetoid_app;

-- budgets: a budgets row is never updated at all (rule B2), so there is no UPDATE grant of
-- any shape. No delete path exists either.
REVOKE ALL ON budgets FROM budgetoid_app;
GRANT SELECT, INSERT ON budgets TO budgetoid_app;

-- accounts: budget_id (tenancy, rule X1), currency_code (rule A1), and created_at_utc are
-- immutable by omission.
REVOKE ALL ON accounts FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON accounts TO budgetoid_app;
GRANT UPDATE (name, type, opening_balance) ON accounts TO budgetoid_app;

-- category_groups: budget_id and created_at_utc immutable by omission.
REVOKE ALL ON category_groups FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON category_groups TO budgetoid_app;
GRANT UPDATE (name, description, position) ON category_groups TO budgetoid_app;

-- categories: budget_id and created_at_utc immutable by omission; category_group_id is
-- updatable — moving a category between groups is a real operation, and the composite
-- foreign key to category_groups (id, budget_id) keeps the move inside the budget.
REVOKE ALL ON categories FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON categories TO budgetoid_app;
GRANT UPDATE (name, description, position, category_group_id) ON categories TO budgetoid_app;

-- payees: no DELETE — no delete path exists. budget_id and created_at_utc immutable by
-- omission.
REVOKE ALL ON payees FROM budgetoid_app;
GRANT SELECT, INSERT ON payees TO budgetoid_app;
GRANT UPDATE (name) ON payees TO budgetoid_app;

-- transactions: budget_id (rule X1) and created_at_utc immutable by omission. The updatable
-- references (account_id, payee_id, category_id) are each half of a composite foreign key
-- that includes budget_id, so a repoint can only land inside the same budget.
REVOKE ALL ON transactions FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON transactions TO budgetoid_app;
GRANT UPDATE (amount, date, description, account_id, payee_id, category_id)
    ON transactions TO budgetoid_app;

-- __EFMigrationsHistory: SELECT lets the role read migration state (applied/pending checks).
-- It is deliberately NOT enough to run MigrateAsync, even as a no-op. Verified empirically
-- against EF Core 10.0.9 + Npgsql 10.0.2 on PostgreSQL 17: the no-op path reads the history,
-- then unconditionally runs CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" — refused with
-- 42501 because PostgreSQL checks CREATE on the schema even when the table already exists —
-- and then takes its migration lock as LOCK TABLE ... IN ACCESS EXCLUSIVE MODE, which needs
-- UPDATE, DELETE, or TRUNCATE on the table. Granting that pair (CREATE on the schema, UPDATE
-- on history) would re-open exactly the holes this role exists to close, so migrations —
-- including the Development startup MigrateAsync — must keep running on the admin connection.
REVOKE ALL ON "__EFMigrationsHistory" FROM budgetoid_app;
GRANT SELECT ON "__EFMigrationsHistory" TO budgetoid_app;

-- Row-level security: which rows the role may reach, where the grants above say which columns it
-- may ever change. It lives in this file and not in a migration for the grants' own reasons (see
-- the header and ADR 0004) — the policies are written TO budgetoid_app, so they are part of that
-- role's privilege story, and keeping them together means a deploy cannot apply the grants and
-- forget the policies.
--
-- The two halves fail in OPPOSITE directions, which is the thing to carry away from here. A table
-- nobody grants is invisible to the role, and the first feature to touch it fails loudly with
-- 42501 — fail-closed. A granted table nobody writes a policy for is fully readable and writable
-- by the role across every tenant, silently — fail-open. So a new budget-owned table needs a grant
-- above AND a policy below; RlsCoverageTests derives its subject from the live schema so that a
-- missing policy fails a test rather than shipping.
--
-- Ownership decides which policy a table owes. A table carrying budget_id is budget-owned and owes
-- budget_isolation; a table carrying user_id — or being users itself, whose own row has no user_id
-- column — is user-owned and owes user_isolation. budget_id wins where both could apply, because a
-- budget belongs to exactly one user and the budget-keyed predicate is therefore strictly narrower.
-- A table carrying neither is refused rather than waved through: nobody can say which of two
-- policies it owes, and "we forgot" and "it needs nothing" leave the identical catalog behind. The
-- column that decides tenancy must also be NOT NULL — under budget_id = current_budget a row whose
-- owner is NULL is invisible to every session, which is fail-closed but undiagnosable.
--
-- The subject is every RELATION in public that can hold or expose rows, not every ordinary table,
-- and the difference is one this file learned the hard way twice. A view is not a table but it
-- exposes rows, and unless security_invoker is set it runs with its OWNER's privileges — the owner
-- being the schema owner, who bypasses row-level security — so a view granted to budgetoid_app
-- reads every tenant while coverage reports green. A materialized view cannot be policed at all. A
-- partitioned parent is invisible as relkind 'p' while its partitions are 'r', and PostgreSQL
-- applies the PARENT's policies to queries routed through the parent, so the relation nobody saw is
-- the one whose policies fire. Views, materialized views and foreign tables are therefore refused
-- outright and have to be exempted by name if one is ever wanted; index, sequence, composite type
-- and TOAST table stay out because they expose no rows of their own, which is the test any future
-- narrowing has to pass.
--
-- Every other table is exempt, and each exemption is written down with its reason rather than
-- falling out of a query by accident:
--
--   credentials           read to discover WHO is asking — a policy keyed on the identity it
--                         resolves would refuse the query that resolves it
--   passkey_public_keys   read to decide whether an assertion's signature is genuine, which the
--                         ceremony answers before it knows whose account it is; held to that reason
--                         by its pinned column set, because a write-once secret would pass any
--                         append-only rule the grants can express
--   webauthn_challenges   a nonce belonging to a ceremony rather than to a person; the
--                         authentication pool is issued before anybody has said who they are
--   currencies            shared reference data belonging to no tenant
--   __EFMigrationsHistory EF's own bookkeeping
--
-- budgets and users used to be on that list, and the reason was real at the time: provisioning read
-- them before any identity existed. It read users only because the credential lookup joined to it
-- for a value the caller then discarded. Reading the credential alone yields the same user id, so
-- the identity is now known before either table is touched — including at registration, where
-- User.Create mints the id on the application side and the row's id therefore exists before the row
-- does. See docs/decisions/0011.
--
-- The reason the list is spelled out rather than inferred: the coverage test used to discover its
-- subjects by looking for a budget_id column, so a new non-tenant table skipped the check without
-- anyone deciding it should. That is the silent half of the asymmetry above, applied to the test
-- meant to catch it. Every table in public is now classified — policed by ownership, or exempt with
-- a reason — and a new one fails the test until someone says which it is.
--
-- That classification lives in Infrastructure/Persistence/Provisioning/RowLevelSecurityCoverage.cs,
-- which RlsCoverageTests and the deploy-time verifier both read. This comment block is a human
-- restatement for whoever is editing this file; it is deliberately not a second EXECUTED list,
-- because two of those have no adjudicator when they disagree and the loser fails open.
--
-- The direction is what matters, so do not "simplify" the exemption list back into a discovery
-- rule. A list of policed tables fails OPEN: the sixth table nobody added to it keeps the suite
-- green. A list of exemptions fails CLOSED: the sixth table is red until someone decides. Same
-- five names either way, opposite properties.
--
-- No table gets FORCE ROW LEVEL SECURITY, and that is a decision rather than an oversight. Owner
-- and superuser bypass is load-bearing here: the schema is created and migrated on the admin
-- connection, test seeding writes both tenants through it, and every read-back that asserts "the
-- other budget's row is untouched" is a question no policed connection could answer. FORCE is the
-- obvious hardening a future reader reaches for; it would break all of that and protect nothing,
-- because the role these policies exist to constrain is not the owner.
--
-- WITH CHECK is a real strengthening rather than a restatement of USING. Today an INSERT lands in
-- whatever budget_id it names — the composite foreign keys only prove the row is internally
-- consistent, not that it belongs to this session's tenant. With WITH CHECK, an insert can only
-- land in the ambient budget.
--
-- These policies are PERMISSIVE, so multiple policies on one table are OR-ed: a second permissive
-- policy can only ever widen what this one allows. Anything meant to NARROW isolation has to be
-- written AS RESTRICTIVE, or it will quietly do the opposite of what it says.
--
-- DROP before CREATE for the same convergence contract as the REVOKE/GRANT blocks above: CREATE
-- POLICY has no OR REPLACE, so a changed policy body only takes effect on a re-run because the old
-- one is dropped first.

-- Two settings, one shape. app.current_budget_id names the ambient budget and app.current_user_id
-- names the authenticated user; SessionContextInterceptor writes both on every connection open, in
-- one round-trip, and writes '' rather than skipping when either is unresolved. Everything the next
-- paragraph argues about the budget setting applies unchanged to the user setting.
--
-- The policies below read the setting as COALESCE(current_setting(..., true), ''), and that shape
-- is load-bearing rather than defensive. A session that names no budget must fail the same way
-- whatever the connection's history: strict current_setting raises 42704 ("unrecognized
-- configuration parameter") on a backend that has never seen the setting, but 22P02 once
-- set_config has run and Npgsql's pool reset has left the parameter defined as ''. The missing_ok
-- overload turns the first case into NULL, and the COALESCE turns that into the same ''::uuid cast
-- as the second — one failure for one bug, which is what RlsIsolationTests pins.
--
-- Note what this is NOT: NULLIF to a NULL comparison, which would make an unset session read as
-- zero rows instead of an error. The '' default is chosen precisely because casting it throws.
--
-- The role default this used to rely on (ALTER ROLE budgetoid_app SET app.current_budget_id = '')
-- cannot be applied here. PostgreSQL requires superuser to set a placeholder parameter — one no
-- loaded extension has registered — as a role default, and no principal on Azure Database for
-- PostgreSQL has superuser, so the statement fails with 42501 for every identity that could run
-- this script. See docs/decisions/0008.

ALTER TABLE accounts ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS budget_isolation ON accounts;
CREATE POLICY budget_isolation ON accounts FOR ALL TO budgetoid_app
    USING      (budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid)
    WITH CHECK (budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid);

ALTER TABLE category_groups ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS budget_isolation ON category_groups;
CREATE POLICY budget_isolation ON category_groups FOR ALL TO budgetoid_app
    USING      (budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid)
    WITH CHECK (budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid);

ALTER TABLE categories ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS budget_isolation ON categories;
CREATE POLICY budget_isolation ON categories FOR ALL TO budgetoid_app
    USING      (budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid)
    WITH CHECK (budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid);

ALTER TABLE payees ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS budget_isolation ON payees;
CREATE POLICY budget_isolation ON payees FOR ALL TO budgetoid_app
    USING      (budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid)
    WITH CHECK (budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid);

ALTER TABLE transactions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS budget_isolation ON transactions;
CREATE POLICY budget_isolation ON transactions FOR ALL TO budgetoid_app
    USING      (budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid)
    WITH CHECK (budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid);

-- The user-owned tables, keyed on the second session setting. Same shape, same reasons, different
-- question: budget_isolation asks which tenant a row belongs to, user_isolation asks which person.
-- A budget belongs to exactly one user, so where both could apply the budget-keyed rule is the
-- narrower one — which is why nothing below carries both.
--
-- These two used to be exempt, and the reason was real: discovery read them before any identity
-- existed. It read them because the credential lookup joined credentials to users for a value the
-- caller then discarded. Reading the credential alone yields the same user id, so the identity is
-- known before any statement below is reached — including on registration, where User.Create mints
-- the id client-side and the id therefore exists before the row does. See docs/decisions/0011.
--
-- credentials keeps the exemption. It is the table read to answer "who is asking", so a policy keyed
-- on the answer would refuse the question that produces it. sessions is the counterexample that
-- keeps that exemption honest: it is read after the question has been answered, so it is policed
-- like everything else, and material attached to a session belongs there rather than on the exempt
-- table.
--
-- passkey_public_keys is the second table with that reason, and the pair below it — the counter,
-- policed — is that same before/after line drawn once more inside a single ceremony. Read the two
-- together before proposing a third exemption: the question is never "is this sensitive" but "is
-- this reachable before the request has an identity", and if the answer is no, a policy costs
-- nothing.

ALTER TABLE users ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS user_isolation ON users;
CREATE POLICY user_isolation ON users FOR ALL TO budgetoid_app
    USING      (id = COALESCE(current_setting('app.current_user_id', true), '')::uuid)
    WITH CHECK (id = COALESCE(current_setting('app.current_user_id', true), '')::uuid);

ALTER TABLE budgets ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS user_isolation ON budgets;
CREATE POLICY user_isolation ON budgets FOR ALL TO budgetoid_app
    USING      (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid)
    WITH CHECK (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid);

-- sessions carries user_id and no budget_id, so it is user-owned by the same rule as budgets, and
-- the classifier reaches that verdict from its columns without being told. A session belongs to a
-- person; the budgets that person owns are reached through their own policies, one layer down.
--
-- The policy deliberately does NOT read the kind column, and no future one may. Whether a session
-- reaches budget content is answered by budget_isolation on the budget-owned tables, which a locked
-- session never satisfies because it resolves no ambient budget. A predicate here consulting kind
-- would be inventing a third isolation axis beside the two this file already carries, and the row a
-- person may see would then depend on which of the three fired last.
ALTER TABLE sessions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS user_isolation ON sessions;
CREATE POLICY user_isolation ON sessions FOR ALL TO budgetoid_app
    USING      (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid)
    WITH CHECK (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid);

-- passkey_signature_counters is the same case as sessions, one step further along the same
-- ceremony: it is reached only after the assertion's signature has verified, so an identity is on
-- the connection by the time this policy is evaluated. Its sibling passkey_public_keys is read one
-- step earlier, with no identity at all, which is why that table is exempt and this one is not. Two
-- tables rather than one is what makes that boundary a thing the schema states instead of a thing a
-- reader has to reconstruct.
--
-- Like sessions, this policy reads only the ownership column. Whether a passkey may be used at all
-- is decided by the ceremony above it, not by a predicate here.
ALTER TABLE passkey_signature_counters ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS user_isolation ON passkey_signature_counters;
CREATE POLICY user_isolation ON passkey_signature_counters FOR ALL TO budgetoid_app
    USING      (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid)
    WITH CHECK (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid);
