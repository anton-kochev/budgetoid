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
-- IT IS THE ONLY TABLE ERASURE NEEDS A NEW GRANT ON, and the grants the cascade does without are a
-- decision rather than an oversight. Every owned table hangs off this row by ON DELETE CASCADE —
-- users → budgets → {payees, accounts, category_groups → categories}, users → credentials →
-- {sessions → session_tokens, passkey_public_keys, passkey_signature_counters,
-- recovery_code_hashes, wrapped_account_keys → key_rotation_seals}, users → key_rotations →
-- key_rotation_seals, and users → factor_manifests — and
-- PostgreSQL performs a
-- referential action through internal triggers that run with the privileges of the REFERENCING
-- table's owner, not of the role that issued the statement. So this one grant empties the
-- structural graph and a grant on any child buys nothing.
-- Database_AllowsDeletingAUserAndCascadesTheAccountAway is what says so: it deletes as this role
-- and asserts every child table is empty afterwards.
--
-- EVERY is a claim rather than a flourish, and it has already been wrong once: wrapped_account_keys
-- landed on the graph and was left out of this rendering, so the sentence read as complete while
-- naming one table fewer than the schema held. A list that reads as complete and is not is worse
-- than no list, because the next person decides whether a child needs a grant by consulting it. So
-- a new table cascading from anything here joins this rendering, at the level ITS OWN foreign key
-- names: session_tokens hangs off SESSIONS rather than off credentials, because
-- (session_id, user_id) → sessions(id, user_id) is what a DELETE FROM sessions takes with it, and
-- flattening it onto the credentials level would misstate that. factor_manifests hangs off USERS by
-- the same rule read the other way: its one foreign key names users, because a manifest lists every
-- factor's public key at once and so belongs to the account rather than to any one credential, and
-- filing it under credentials would misstate what a DELETE FROM credentials takes with it.
-- key_rotation_seals appears TWICE in that rendering, and the repetition is the rule applied rather
-- than a slip: it carries two foreign keys — (factor_id, user_id) → wrapped_account_keys and user_id →
-- key_rotations — so both a DELETE FROM wrapped_account_keys and a DELETE FROM key_rotations take its
-- rows with them, and a rendering naming only one of the two would misstate what each statement does.
-- key_rotations itself moved up a level in this rendering when it stopped carrying factor_id: its one
-- remaining foreign key names users, so an account erasure reaches it in one hop rather than through
-- the wrapped keys. PostgreSQL permits the two cascading paths into key_rotation_seals that this
-- creates; the multiple-cascade-path restriction is SQL Server's, not this server's.
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
-- The children holding no DELETE of any shape are budgets, payees, sessions, session_tokens,
-- passkey_public_keys, passkey_signature_counters, wrapped_account_keys, key_rotations,
-- key_rotation_seals and factor_manifests. Three of them are the ones to read carefully, and they span
-- the whole width of the list. key_rotations holds SELECT, INSERT and a column-listed UPDATE and
-- still no DELETE of any shape, so it belongs here rather than being mistaken for a write-free table;
-- ITS OWN block argues why staging needs the insert and the update together and why the delete waits
-- for the completion step that clears the staging. factor_manifests holds SELECT, INSERT and a
-- column-listed UPDATE (manifest, rotation_epoch) — the insert is registration writing the account's
-- first manifest in the same save as the account, the update is a promotion rewriting that one row in
-- place — and still no DELETE at all, because a manifest leaves only by the cascade from users. It
-- belongs here for the same reason the first one does: this is a list of absent DELETEs, not a list of
-- read-only tables and not a list of write-free ones.
-- key_rotation_seals holds SELECT and nothing else on the same reasoning and
-- is the newest arrival; its own block says which write it expects and which caller has to bring it.
-- That is a list of the
-- same kind as the cascade rendering above and carries the same obligation — it is exhaustive or it
-- is misleading, and this is the list somebody consults to decide whether a child needs a grant.
-- Two of those absences would cost something real to fill.
-- passkey_public_keys is exempt from row-level security — it is read before the request has an
-- identity a policy could key on — so a DELETE there would be UNPOLICED, and one statement carrying
-- the wrong id would remove somebody else's only way in with nothing to catch it. That is the exact
-- hazard credentials' own DELETE carries since ADR 0014, bounded by the application and by nothing
-- beneath it, which is why that grant took an argument of its own rather than a precedent. On
-- passkey_signature_counters a DELETE would reopen counter rewind: removing the row and
-- re-inserting it at zero is what the deliberately single-column GRANT UPDATE (signature_counter)
-- exists to forbid. The other absences are argued in their own blocks rather than here, and
-- session_tokens' and wrapped_account_keys' share one sentence: with DELETE granted, an EF cascade
-- into rows the change tracker happens to be holding succeeds SILENTLY, and without it the same
-- mistake dies loudly with 42501. The cascade reaches all of them as the table owner, which is
-- scoped by the row it descends from rather than by a privilege. Do not "complete" this set: adding
-- a grant to a child widens the role's reach without extending what erasure can do.
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
-- row of this table is owner-and-type-scoped (FindPasskeyCredentialAsync and
-- FindRecoveryCodeCredentialAsync are reads of that shape; a Credential built by hand gets a fresh
-- id from its factory, so it names no row here and its delete matches nothing);
-- credentials.user_id is immutable, so an id cannot change owner between that read and the write;
-- and the two share one transaction. Note the first leg is a REVIEW rule over every repository that
-- can return a Credential, rather than something the type system holds — an unscoped query added to
-- any of them widens this grant back to the whole table, and no test below the application would
-- say so. See docs/decisions/0014,
-- which also records why policing this table instead is not available — RLS is enabled per table
-- and not per command, so a FOR DELETE policy alone would refuse the discovery SELECT that the
-- exemption exists for.
--
-- Database_LetsTheAppRoleDeleteAnyCredential_OnASessionNamingNobody states the unbounded half as an
-- executable test. What notices the application's scoping going is any test that acts for one
-- account and then looks at another's rows —
-- Revocation_OfAnotherAccountsCredential_IsRefusedAndRemovesNeitherAccountsRows and
-- Generation_ForOneAccount_LeavesAnotherAccountsSetWhereItWas are written that way, and every path
-- reaching this grant owes one. Erasure does not use this
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

-- session_tokens: the SHA-256 of the handle one session is presented by, and the session it opens.
-- The token itself never lands here, so a copy of this table — a backup, a replica, one unbounded
-- read — yields digests of 256-bit uniform values and no cookie anybody can present.
--
-- EXEMPT from row-level security, and it is the sharpest instance of the argument credentials,
-- passkey_public_keys and recovery_code_hashes already carry: the row is found BEFORE the request has
-- an identity a policy could be keyed on, because finding it IS how the identity is established. The
-- table it would otherwise have been a column on is sessions, one block above, which is policed by
-- user_isolation keyed on app.current_user_id — exactly the value the lookup exists to produce. A
-- column there would be read by a statement the policy refuses, and refuse it loudly: an unset setting
-- reaches the policy as ''::uuid and raises 22P02, so it would fail on EVERY authenticated request
-- rather than on one route. So the discovery key goes on its own exempt table and everything read
-- after the answer — the expiry, the revocation instant — stays on the policed one. That split is
-- docs/decisions/0012's, applied a fourth time.
--
-- NO UPDATE OF ANY SHAPE, and it is a property of the table rather than a column list somebody could
-- widen. Every column is written whole when the session is established: a digest cannot be edited into
-- another digest that means anything, and repointing session_id or user_id would hand one browser's
-- cookie a different account. There is no column here an edit could reach, so there is no list — which
-- is checkable in one statement, the way recovery_code_hashes' absent UPDATE is.
--
-- NO DELETE, AND THE ABSENCE IS LOAD-BEARING. Rows leave by the ON DELETE CASCADE from sessions, and
-- through it from credentials and users, which runs with the referencing table owner's privileges
-- rather than this role's. That asymmetry is what ADR 0017 argues for and ADR 0018 restates: with
-- DELETE granted, an EF cascade into rows the change tracker happens to be holding succeeds SILENTLY
-- and the rows leave by the application instead of by the database, with no SQLSTATE to say so;
-- without it, the same mistake dies loudly with 42501. On this table the silent version would be
-- worse than on either of those — it is the shape a "sign this browser out" path reaches for, and it
-- would end access while leaving nothing that says when, which is the one thing the sessions block
-- above exists to refuse. Signing out is stamping revoked_at_utc on the session, not removing its
-- handle.
--
-- INSERT HAS TWO WRITERS, AND WHAT THEY SHARE IS THE RULE WORTH WRITING DOWN HERE: a token row is
-- written in the SAME SaveChanges as the session it opens. SessionRepository.AddAsync takes the pair
-- and has no overload taking a session alone, and RegistrationRepository.RegisterAsync carries both
-- among the rows one registration commits at once. So there is no port method that writes a token by
-- itself, and there must not be one — a token committed apart from its session names a session that
-- may never exist, and a session committed without one is an account no cookie can be issued over.
--
-- Note what a column added here would land on, because it is the same mismatch as credentials': the
-- exemption is granted to one QUERY and applied by PostgreSQL to the whole TABLE. The pinned column
-- set in RowLevelSecurityCoverage holds it to its reason, and the columns it pins are what the lookup
-- needs before an identity exists. A last-used instant is the one this table will be offered first,
-- and an expiry is the second; both are read AFTER the token has answered who is asking, so both
-- belong on sessions, which the coverage rule polices by itself. When the pin goes red the fix is to
-- MOVE THE COLUMN, never to widen the pin.
REVOKE ALL ON session_tokens FROM budgetoid_app;
GRANT SELECT, INSERT ON session_tokens TO budgetoid_app;

-- passkey_public_keys: everything on this table is read to decide whether the signature on an
-- assertion is genuine, which a WebAuthn ceremony has to answer BEFORE it knows whose account it is.
-- So it is exempt from row-level security for the same reason credentials is, and it carries the
-- same kind of pinned column set.
--
-- What holds this exemption to its reason is the PINNED COLUMN SET in RowLevelSecurityCoverage, not
-- the two grant lines below. Read that carefully, because the appealing answer is the wrong one. The
-- hazard is not mutation: the exemption is granted to a QUERY and applied to the whole TABLE, so
-- every column here is readable by every application session whoever that session names. A
-- recovery-code hash is written once and never updated — it satisfies an append-only rule perfectly,
-- and this table, already holding key material, is the most attractive place in the schema to propose
-- one. The pin is what turns a new column red; the answer to that red is to MOVE THE COLUMN to a
-- table carrying user_id, never to widen the pin.
--
-- An account-key secret used to stand beside the hash in that sentence and no longer does: this role
-- took UPDATE (encapsulated_account_keys) on wrapped_account_keys for a content-key rotation's
-- promotion, so one of the two secrets stopped being write-once. That SHARPENS the paragraph rather
-- than weakening it. A screen a later GRANT can revoke was never what was deciding, and an append-only
-- test would now admit the hash and reject the encapsulated value — catching one of them, for a reason
-- unrelated to why either is dangerous here.
--
-- The grants are the corollary, and worth having because they are checkable in one line: no UPDATE of
-- any shape and no DELETE, ever, so mutable per-user state cannot accumulate here. credentials is
-- exempt on a table that could one day take an UPDATE column list; this one cannot. That is a real
-- narrowing — it just does not cover the case that actually threatens the exemption, which is a
-- secret READ BY EVERY SESSION, whether or not that secret ever changes. The older wording here said
-- "a secret that never changes", and wrapped_account_keys taking an UPDATE is what showed that to be
-- the wrong half of the property: a rotating secret on this table would be exactly as exposed as a
-- frozen one, because the exemption is about who may read the table and never about who may write it.
-- See docs/decisions/0012.
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
-- Among the identity tables — users, credentials, sessions, passkey_public_keys,
-- passkey_signature_counters, recovery_code_hashes and this one — DELETE is granted only where
-- removing the row IS the operation, and each grant below says which operation that is. This one,
-- because these rows are nonces:
-- consuming one IS deleting it, which is the property that makes a challenge single-use, and a row
-- nobody can delete is a row swept by a path that does not exist. recovery_code_hashes, because that
-- same sentence is true of a recovery code word for word — see its block. credentials, because
-- revoking a passkey removes the row rather than marking it, scoped by the application and by
-- nothing beneath it — see ADR 0014. users, because it is the root every other owned row cascades
-- from, so deleting it is how an account is erased — see that block for why the cascade means the
-- tables in between need no grant of their own. Contrast the sessions block, where revocation writes
-- a column precisely so the row stays accountable — opposite decisions, because the rows mean
-- opposite things.
--
-- (Read the GRANT lines rather than this paragraph for which tables hold DELETE: prose here executes
-- nothing, so it can only ever be a restatement of them, and a count of them was wrong twice before
-- it was dropped.)
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
-- Exempt from row-level security, resting on the same argument credentials and passkey_public_keys
-- rest on: the row is found before the request has an identity a policy could be keyed on. A
-- recovery code is redeemed ANONYMOUSLY — somebody redeeming one has lost the
-- authenticator that would have proved who they are — so the lookup by hash is what establishes the
-- identity, and a policy keyed on app.current_user_id would refuse the very query that produces the
-- value it wants to compare against. It would refuse it loudly rather than quietly: an unset setting
-- reaches the policy as ''::uuid and raises 22P02 on every redemption. That is the trap ADR 0012
-- records, and this table walks up to it exactly as the other two do.
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
-- ONE PATH EXERCISES THIS GRANT, and there must never be a second.
-- RecoveryCodeRepository.ConsumeAsync, reached from POST /api/recovery-codes/redemption, removes the
-- single row whose verifier_hash the caller's own verifier hashes to. Every leg of the paragraph above
-- is checkable there: the entity comes from IRecoveryCodeRedemption.FindByVerifierHashAsync, the
-- delete takes that entity rather than bytes, and both statements run inside the handler's one
-- transaction. Regenerating a set does NOT use this grant — it deletes the SET's credentials row and
-- these rows leave by the cascade from it, which runs with the referencing table owner's privileges,
-- and materialising them there is the mistake GenerateRecoveryCodesHandler is written to avoid. A
-- reader looking for a second caller will not find one, and should not add one.
--
-- Note what a column added here would land on, because it is the same mismatch as credentials': the
-- exemption is granted to one QUERY and applied by PostgreSQL to the whole TABLE. The pinned column
-- set in RowLevelSecurityCoverage is what holds it to its reason, and the columns it pins are what
-- the lookup needs before an identity exists. A wrapped key is the column this table was offered
-- first, and it is the worked example rather than the hypothetical one: the account's keys are wrapped
-- under every recovery factor, a set's ten codes are ten factors, and their rows are already here — so
-- filing the ten wrapped copies beside them reads as the obvious thing to do. It was the wrong place,
-- for the reason the pin exists: a wrapped key is read AFTER redemption has answered who is asking, so
-- it belongs on a table carrying user_id, which the coverage rule polices by itself with no argument
-- needed. It went to wrapped_account_keys, the block directly below, which opens by saying so. When
-- the pin goes red the fix is to MOVE THE COLUMN, never to widen the pin.
REVOKE ALL ON recovery_code_hashes FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON recovery_code_hashes TO budgetoid_app;

-- wrapped_account_keys is where the column the block above refuses ended up, and it is policed
-- rather than exempt for the reason that block, RowLevelSecurityCoverage and
-- docs/engineering/data-isolation.md all give: a wrapped key is read AFTER the request has an
-- identity, so it belongs on a table carrying user_id, which the coverage rule polices by itself.
-- The table therefore needs no entry in RowLevelSecurityCoverage.Exemptions — that absence is the
-- mechanism working, not an omission, and it is the whole of the argument. Nothing is re-argued here.
--
-- SELECT IS GRANTED FOR TWO KINDS OF READER, and this file's header tells the next reader to grant
-- only what a table needs, so both are named rather than left to look like coverage. Neither makes
-- the other redundant, and withdrawing the grant takes both down together.
--
-- The application reader is GET /api/me/account-keys, which hands a signed-in browser the wrapped
-- private key and the encapsulated account keys of EVERY FACTOR THE AUTHENTICATED ACCOUNT HOLDS — one
-- row per registered passkey and ten per set of recovery codes, so eleven for an ordinary account,
-- because a factor is not a credential. It reaches the table through AccountKeyReadService.ListForAccountAsync,
-- which PROJECTS and materialises no entity, for the reason the NO DELETE block below gives.
-- The statement's only predicate is the owner, which is also the seek: IX_wrapped_account_keys_user_id
-- exists for exactly it.
--
-- KEYED ON THE ACCOUNT AND NOT ON THE SESSION'S CREDENTIAL, and that is the correction of a shape this
-- file used to argue for. The old argument was that a browser can only ever hold a key-encryption key
-- derived from the factor its own session was opened with, so a wider answer would be material
-- travelling further than it is needed. That is false, and two things in the repository say so:
-- PasskeyReauthentication looks a passkey up BY ACCOUNT — FindByWebAuthnCredentialIdForUserAsync takes
-- IUserContext.UserId, never the session's credential — and the assertion options carry NO
-- allowCredentials, so the AUTHENTICATOR chooses which of the account's credentials answers a ceremony.
-- Redeem a recovery code, then ask for a new set (which is gated on a fresh passkey assertion) and the
-- narrowed read hands back the ten code envelopes while the browser is holding the passkey's key. Every
-- row correct, a 200, and an account that will not open; nothing on the server sees it.
--
-- What the widening costs is real and is accepted. A caller now receives entries it holds nothing to
-- open. The operator already holds every one of these rows, so nothing is disclosed to the party this
-- table's policy defends against, and a factor's envelopes open ONLY under a key-encryption key derived
-- from that factor — a secret this database has never stored in any form.
--
-- The other readers are the row-level-security isolation tests, and an endpoint answering correctly
-- is not evidence about them: a policed table whose isolation nothing exercises is a policy nobody
-- has watched fire.
-- Database_HidesAnotherAccountsWrappedKeys_FromASessionNamingThisUser is the one this grant exists
-- for: it reads the table as this role on a session naming one owner and asserts that the other
-- owner's row is not there, which is the only statement in the system that has watched user_isolation
-- decide anything here. Database_RefusesAWrappedKeyReadOnASessionNamingNobody needs the grant too,
-- for the 22P02 an unset app.current_user_id reaches the policy as. Both go red the day the grant is
-- withdrawn and the day the policy is, which is what makes this paragraph a claim about the
-- repository rather than a plan for one.
--
-- ONE UPDATE, ONE COLUMN, ONE CALLER — AND THE SHAPE IS FORCED RATHER THAN CHOSEN. Registering or
-- revoking a recovery factor rewrites a factor's own row and is not a key rotation (FR-101), so those
-- paths still write a new row rather than editing one. A content-key rotation (FR-080) is the single
-- operation that rewrites a stored value in place, and its promotion rewrites one row per surviving
-- factor, copying that factor's staged key_rotation_seals value into this column.
--
-- Every other way of retiring that row is closed, which is what makes this a forced shape rather than
-- a convenience. Deleting its credentials row would destroy the passkey registration itself. Inserting
-- a replacement under a new factor_id and deleting the old needs a DELETE this table must never hold,
-- for the reason the next paragraph gives. Inserting a replacement and leaving the old row behind is
-- permanent litter on GET /api/me/account-keys — one dead entry per rotation, on the one route a
-- browser uses to find the row it can open, every entry of which it must try in turn. The in-place
-- UPDATE is what is left, and it is narrowed to the ONE column a promotion writes: factor_id,
-- credential_id, user_id, credential_type and created_at_utc stay immutable by their absence from this
-- list, as they always did.
--
-- THE LIST LOST A COLUMN AND THAT IS A GENUINE NARROWING RATHER THAN A RENAME. It used to name both of
-- this table's payload columns, because both held an account key wrapped under the factor's
-- key-encryption key and a rotation re-wrapped both. The row now holds two values of two different
-- kinds, and only one of them moves: encapsulated_account_keys carries the account's content and index
-- keys encapsulated TO the factor's public key, so a new generation replaces it; wrapped_private_key
-- carries the factor's OWN private key wrapped UNDER the key-encryption key that factor derives, and a
-- rotation changes neither of those, so the value is byte-for-byte what it was. WRAPPED_PRIVATE_KEY IS
-- THEREFORE IMMUTABLE BY OMISSION — rule B2 at the head of this file, the same mechanism holding
-- users.email's four siblings — and the omission is doing real work: it is the column whose loss would
-- leave a factor able to prove itself and unable to open anything, and no path in the product has any
-- reason to write it twice.
--
-- ONE COLUMN, SO THE PROBE IS ONE STATEMENT. The pairing argument that stood here — a rotation assigns
-- both properties together, EF emits one UPDATE naming both columns, so a one-column grant would fail
-- the whole statement with 42501 — is retired by the shape rather than by a decision, because there is
-- only one column left to name. What survives it is the measurement rule: a probe must assert the
-- permitted write AND the refused one, or a test cannot tell a one-column grant from a table-wide one.
--
-- The ten wrapped rows of a REPLACED RECOVERY-CODE SET are not this grant's business and never become
-- it. A rotation mints a fresh set rather than re-wrapping the old codes — the browser has never seen
-- the card — and the retired set's rows leave by the cascade below, exactly as they do today.
--
-- NO DELETE, AND THE ABSENCE IS LOAD-BEARING. Revoking a factor removes its wrapped keys by the
-- ON DELETE CASCADE from credentials, which runs with the referencing table owner's privileges rather
-- than this role's — so the cascade succeeds while this role cannot issue the statement itself. That
-- asymmetry is what ADR 0017 argues for: with DELETE granted, an EF cascade into rows the change
-- tracker happens to be holding succeeds SILENTLY, and the rows leave by the application instead of
-- by the database with no SQLSTATE to say so; without it, the same mistake dies loudly with 42501.
--
-- The concrete consequence, because it now binds a SECOND table: GenerateRecoveryCodesHandler must
-- never materialise the replaced set's child rows. It already carries that rule for
-- recovery_code_hashes — where the role DOES hold DELETE, which is exactly why the mistake would be
-- silent there — and the rule now covers this table too. A future "load the wrapped keys so we can
-- count them" read on that path is the way it breaks. RevokePasskeyHandler and EraseAccountHandler
-- document the identical mechanism for their own tables.
REVOKE ALL ON wrapped_account_keys FROM budgetoid_app;
GRANT SELECT, INSERT ON wrapped_account_keys TO budgetoid_app;
GRANT UPDATE (encapsulated_account_keys) ON wrapped_account_keys TO budgetoid_app;

-- key_rotations: SELECT and INSERT, plus an UPDATE over every column BUT the primary key. This block
-- used to say the write privileges were waiting for their callers; the caller arrived, so the
-- argument moves here rather than being deleted — a grant whose reason was only ever "nothing uses
-- it yet" has no reason at all once something does.
--
-- SELECT was granted before any of it, for the reason ADR 0018 settled on wrapped_account_keys when
-- it granted SELECT to the row-level-security probes ahead of any production reader: AN UNGRANTED
-- UPDATE LEAVES NOTHING UNOBSERVABLE; AN UNGRANTED SELECT DOES. Measured rather than argued from
-- precedent — with no SELECT, NarrativeSecrecyTests' plaintext scan runs over the catalog on the
-- app-role connection, meets 42501 on this table, and reports it as UNSCANNABLE. Two secrecy gates
-- then pass while covering one table fewer than the schema holds, which is the same defect as a
-- census that reads as complete and is not. A table nothing can read is a table nothing can check.
--
-- INSERT AND UPDATE ARRIVE TOGETHER BECAUSE STAGING IS AN UPSERT, and the pairing is forced by the
-- primary key rather than chosen for symmetry. user_id is the whole of PK_key_rotations, so an
-- account holds at most one rotation in flight as a KEY rather than as a rule nobody executes. Begin
-- is also the repair path — a completion refused because the live factor set moved is answered by
-- beginning again with the corrected set — so a second begin has to go through, and going through
-- means rewriting the row that is already there. KeyRotationRepository.StageAsync finds and updates
-- for exactly this reason and says so at its own call site.
--
-- THE LIST NAMES WHAT A SECOND BEGIN REWRITES, AND THE COLUMNS IT NAMES MOVED WITH THE TABLE. It used
-- to cover factor_id and the two wrapped envelopes; a rotation is no longer performed under one factor
-- and no longer stages a wrapped key here, so the staged material is now the next generation's factor
-- manifest and the epoch it will be filed at. The per-factor values of a run live on
-- key_rotation_seals, whose own block below explains why it holds no write privilege yet.
--
-- THE COLUMN LIST OMITS user_id, AND THAT OMISSION IS THE IMMUTABILITY. PostgreSQL column privileges
-- are additive and REVOKE UPDATE (col) cannot subtract from a table-wide grant, so the only spelling
-- that makes a column unwritable is leaving it out of the list — rule B2 at the head of this file,
-- and the same mechanism holding users.email's four siblings and sessions' five. A table-wide GRANT
-- UPDATE ON key_rotations would let a staged rotation be reassigned to another account in one
-- statement, on a table whose whole purpose is to hold the next generation of somebody's keys.
--
-- NO DELETE, AND THE ABSENCE IS LOAD-BEARING RATHER THAN PENDING. Completion is what ends a rotation
-- and completion is unbuilt; when it lands it will need DELETE and will bring its own argument. Until
-- then the missing privilege is what stops a half-written promotion path from clearing the staging
-- before it has promoted anything — the one destruction on this whole path that has no repair, since
-- the staged envelopes are the only copies of the new generation until the live row is overwritten.
-- It fails loud: 42501 on the first reach, which is a test failing rather than a rule going quiet.
--
-- The table is POLICED rather than exempt: it carries user_id, so user_isolation appends the owner
-- to every statement against it. See the policy at the foot of this file.
--
-- IT NOW HANGS OFF users DIRECTLY, WHICH IS WHAT KEEPS AN ERASURE ABLE TO REACH IT. Its only foreign
-- key used to be the composite one to wrapped_account_keys, and that left with factor_id; a table on
-- no edge at all is a table the cascade from users never reaches, so an erased account would have left
-- a staging row behind carrying its own user id. Since this role holds no DELETE here, nothing in the
-- application could have cleaned it up either.
REVOKE ALL ON key_rotations FROM budgetoid_app;
GRANT SELECT, INSERT ON key_rotations TO budgetoid_app;
GRANT UPDATE (rotation_id, staged_manifest, staged_rotation_epoch, started_at_utc)
    ON key_rotations TO budgetoid_app;

-- key_rotation_seals: SELECT AND NOTHING ELSE. One row per surviving factor per run, holding the next
-- generation's content key and index key as one value, encapsulated TO that factor's public key — the
-- value a promotion copies into wrapped_account_keys.encapsulated_account_keys. The verb matters and
-- is not interchangeable with the two beside it: nothing here is wrapped, because the factor's private
-- key is not the run's to touch, and nothing here is sealed, because the plaintext is keys rather than
-- content.
--
-- SELECT FIRST, BECAUSE AN UNGRANTED SELECT IS THE ONE ABSENCE THAT HIDES SOMETHING. Measured on
-- key_rotations rather than argued from precedent: with no SELECT, NarrativeSecrecyTests' plaintext
-- scan runs over the catalog on the app-role connection, meets 42501 on the table, and reports it as
-- UNSCANNABLE. Two secrecy gates then pass while covering one table fewer than the schema holds, which
-- is the same defect as a census that reads as complete and is not. A table nothing can read is a table
-- nothing can check — and this one holds key material, which is the last place to accept a silent gap.
--
-- NO INSERT, NO UPDATE, NO DELETE, BECAUSE NOTHING WRITES A SEAL YET, AND THE ASYMMETRY WITH THE SELECT
-- ABOVE IS THE WHOLE ARGUMENT. An ungranted write leaves nothing unobservable: it fails loud on the
-- first reach — 42501, on the statement that wanted it, in the test that exercises the path — where an
-- ungranted read fails quiet by turning a scan into a skip. Withholding a write therefore costs
-- nothing, and granting one ahead of its caller buys nothing but reach. THE TABLE EXISTS TO BE WRITTEN,
-- and by a path this repository does not have: the continue leg of a rotation, which encapsulates the
-- new generation to every public key the staged manifest names and files one of these rows per factor.
-- Whoever brings that leg brings INSERT, and brings it with the sentence saying which operation needs
-- it — not now, by whoever is in a hurry. A DELETE is not obviously owed at all: a superseded run's
-- seals leave by the ON DELETE CASCADE from key_rotations when a second begin replaces the staging row,
-- which runs with the referencing table owner's privileges rather than this role's, so the rows go
-- while this role still cannot issue the statement. That asymmetry is the one ADR 0017 argues for, and
-- it is why an EF cascade into rows the change tracker happens to be holding dies loudly here with
-- 42501 instead of succeeding in silence.
--
-- THERE IS NO GRANT UPDATE OF ANY SHAPE HERE, SO THERE IS NO COLUMN LIST EITHER, AND THAT IS WORTH
-- SAYING RATHER THAN LEAVING AS AN ABSENCE. Immutability in this file is expressed by OMISSION FROM A
-- GRANT UPDATE COLUMN LIST — never by REVOKE, which cannot subtract from a table-wide grant, and never
-- by widening a list to table-wide (rule B2 at the head of this file). Today every column of this table
-- is immutable in the strongest available way, because no UPDATE exists to name one — and a seal has no
-- edit that means anything: a run that wants a different value for a factor has staged the wrong one
-- and is replaced whole. If an UPDATE is ever wanted it takes encapsulated_account_keys alone; user_id
-- and factor_id stay off it, or one statement could re-file an account's staged generation against
-- another account's factor.
--
-- The table is POLICED rather than exempt: it carries user_id, so the coverage classifier reaches that
-- verdict from the columns without being told, and user_isolation appends the owner to every statement
-- against it. Nothing here is read before the request has an identity. See the policy at the foot of
-- this file.
REVOKE ALL ON key_rotation_seals FROM budgetoid_app;
GRANT SELECT ON key_rotation_seals TO budgetoid_app;

-- factor_manifests: SELECT, INSERT AND A TWO-COLUMN UPDATE. The table is one row per account holding
-- the authenticated list of every recovery factor's public key — the value a client reads to learn
-- which factors exist and what to encapsulate the account's keys to.
--
-- SELECT FIRST, BECAUSE AN UNGRANTED SELECT IS THE ONE ABSENCE THAT HIDES SOMETHING. Measured on
-- key_rotations rather than argued from precedent: with no SELECT, NarrativeSecrecyTests' plaintext
-- scan runs over the catalog on the app-role connection, meets 42501 on the table, and reports it as
-- UNSCANNABLE. Two secrecy gates then pass while covering one table fewer than the schema holds, which
-- is the same defect as a census that reads as complete and is not. A table nothing can read is a table
-- nothing can check — and this table is one nobody would want unchecked, because it is the one place
-- an account's factor set is written down.
--
-- INSERT, BECAUSE REGISTRATION NOW WRITES THE FIRST MANIFEST. That is the sentence this block used to
-- promise it would carry the day a caller arrived, and the caller is RegisterAccountHandler: the
-- account's first manifest is built at rotation epoch 1 and rides the SAME SaveChanges as the user, its
-- credentials and the eleven wrapped_account_keys rows the manifest names. There is no transaction on
-- that path — see the 22P02 argument at RegisterAccountHandler — so the atomicity is the single save's,
-- and an ungranted INSERT here would turn the whole registration into a 42501 rather than losing a row
-- quietly. The statement is policed: user_isolation's WITH CHECK compares user_id against
-- app.current_user_id, which that handler publishes before the save opens the connection.
--
-- UPDATE (manifest, rotation_epoch), BECAUSE PROMOTION REWRITES THIS ROW IN PLACE. The table is keyed
-- on user_id alone, so a generation cannot be modelled as one row per epoch with the newest winning —
-- the newer manifest replaces the older one in the row that already exists, and that statement is an
-- UPDATE or it is nothing.
--
-- THE COLUMN LIST IS THE ENFORCEMENT, AND user_id IS OFF IT DELIBERATELY. Immutability in this file is
-- expressed by OMISSION FROM A GRANT UPDATE COLUMN LIST — never by REVOKE, which cannot subtract from a
-- table-wide grant, and never by widening a list to table-wide (rule B2 at the head of this file). A
-- table-wide GRANT UPDATE ON factor_manifests would let ONE statement re-file an account's whole factor
-- set against another account: the owner column is the primary key and the tenancy column at once, so
-- moving it moves every public key on the row and nothing else on the row would look wrong afterwards.
-- user_isolation's WITH CHECK refuses that row today, and leaning on it would still be the wrong call:
-- grants fail CLOSED and row-level security fails OPEN, so a policy dropped, disabled or bypassed
-- leaves nothing in the way, while an ungranted column answers 42501 whatever else has gone missing.
-- Two independent halves, and this is the one that does not depend on the other being there.
--
-- THE GRANT IS AHEAD OF ITS CALLER AND THAT IS ON PURPOSE, the same call budgets' one-column UPDATE
-- makes further down. FactorManifest.Promote is the domain step — it refuses an epoch that is not
-- exactly one greater than the loaded row's, and EF's concurrency token on rotation_epoch refuses a
-- second promotion started from the same generation — but no handler calls it yet. A privilege added at
-- the moment its first caller appears is a privilege added by whoever is in a hurry; this one is
-- decided here, in the file that is the only place a privilege may be decided.
--
-- STILL NO DELETE, and that absence has a different reason from the UPDATE's arrival rather than the
-- same one. DELETE has no caller in view at all — a manifest leaves by FK_factor_manifests_users
-- cascading from an account erasure, and a referential action runs with the referencing table's owner's
-- privileges rather than this role's, so the row goes without the role ever holding the command.
--
-- The table is POLICED rather than exempt: it carries user_id, so the coverage classifier reaches that
-- verdict from the columns without being told, and user_isolation appends the owner to every statement
-- against it. Nothing here is read before the request has an identity. See the policy at the foot of
-- this file.
REVOKE ALL ON factor_manifests FROM budgetoid_app;
GRANT SELECT, INSERT ON factor_manifests TO budgetoid_app;
GRANT UPDATE (manifest, rotation_epoch) ON factor_manifests TO budgetoid_app;

-- budgets: ONE UPDATE, ONE COLUMN, and it is the exception ASM-004 names rather than a softening of
-- rule B2. No command may change a budget's name: it is sealed once, at creation, and no route accepts
-- a rename. The one operation that must rewrite it is a content-key rotation (FR-099), which re-seals
-- the same text under a new key rather than changing what the text says — which is why ADR 0004 now
-- states B2 as "no command updates a budgets row" instead of "a budgets row is never updated at all".
--
-- The column list is the enforcement. user_id, base_currency_code, created_at_utc and id are immutable
-- by their absence from it, and a table-wide GRANT UPDATE ON budgets would reopen all four at once to
-- buy nothing — the mistake the header of this file explains cannot be undone with REVOKE.
--
-- Every budgets row holds NULL in name today, because registration writes the nameless budget and
-- naming is unbuilt, so this grant is exercised only by a test that seeds a named budget. It is
-- granted now because FR-099 requires it now, and because a privilege added at the moment its first
-- caller appears is a privilege added by whoever is in a hurry.
--
-- No delete path exists.
REVOKE ALL ON budgets FROM budgetoid_app;
GRANT SELECT, INSERT ON budgets TO budgetoid_app;
GRANT UPDATE (name) ON budgets TO budgetoid_app;

-- accounts: budget_id (tenancy, rule X1), currency_code (rule A1), and created_at_utc are
-- immutable by omission.
--
-- A NAME AND ITS INDEX MOVE TOGETHER OR NOT AT ALL, and that is a rule about blind-indexed name
-- columns generally rather than a fact about this table. Such a column is a pair: the ciphertext
-- nobody here can read, and the keyed digest that is the only way a row holding a given name can be
-- found or refused as a duplicate. Both are computed from one piece of text by one client and written
-- by one statement, so an UPDATE list naming one of them narrows nothing — it forbids the operation
-- both columns exist to serve, or it permits half of it, and those are the only two outcomes.
--
-- Withholding name_key was the first of those, and it was live: Account.Update assigns both properties
-- from a single IndexedName, EF emits one UPDATE naming both columns, PostgreSQL refuses the whole
-- statement with 42501, and renaming an account was therefore impossible for this role. The other
-- direction is worse, because it raises nothing: the row would keep a digest taken over a name it no
-- longer holds, the unique index would go on policing the name that left, a search for the new name
-- would miss the row that has it and a rename onto a name already taken would be accepted. Nothing on
-- this side notices — recomputing either half needs the account's index key, which lives in a browser.
--
-- So the pair travels together in every direction: granted together, withheld together, and made
-- immutable, if it ever is, by leaving BOTH off the list. Every blind-indexed name column that follows
-- wants the same pair, and a review that finds one half of one on an UPDATE list has found the defect
-- without needing to know which table it was looking at.
REVOKE ALL ON accounts FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON accounts TO budgetoid_app;
GRANT UPDATE (name, name_key, type, opening_balance) ON accounts TO budgetoid_app;

-- category_groups: budget_id and created_at_utc immutable by omission.
--
-- name and name_key are the pair the accounts block above argues at length; that argument is not
-- restated here, only pointed at, because it is a rule about blind-indexed name columns generally
-- and a third copy is a third thing to correct the day it changes. This table is the third column
-- to want it.
--
-- WHAT IS NEW HERE, AND IT MAKES A HALF GRANT HARDER TO SEE THAN ON ACCOUNTS OR PAYEES: this
-- table's update writes THREE sealed-or-keyed columns — TWO narrative ones, `name` and
-- `description`, and the blind index `name_key`, which is not narrative and must not be called
-- one: NarrativeField types exactly `name` and `description`, and KeyMaterialSecrecyTests gives
-- the index a kind of its own — and EF names only the ones that changed. On
-- accounts and payees the update assigns both halves of the name from one IndexedName every time,
-- so any rename issues a statement naming both columns and a half grant refuses the whole of it
-- with 42501 — the defect is loud on the first rename anybody exercises. Here a rename that leaves
-- the description alone emits two columns and SUCCEEDS under a grant missing `description`.
--
-- Measured on postgres:17.10 under GRANT UPDATE (name, name_key, position) — description withheld:
--   update ... set name = ..., name_key = ...                     -> UPDATE 1
--   update ... set name = ..., name_key = ..., description = ...  -> 42501, permission denied
--   update ... set description = null                             -> 42501
--   update ... set position = 3                                   -> UPDATE 1
-- So a route case that renames a group whose description is unchanged is green under a broken
-- grant; only a case that writes the name AND the description catches it, and only a raw statement
-- naming all four catches a missing `position` as well. The controls for this line are written to
-- that shape deliberately.
--
-- SELECT, INSERT and DELETE are left table-wide, which is why name_key needed no edit there:
-- PostgreSQL's table-level privilege covers every column, including ones added later. Only the
-- column-list grant had to move. Do not "simplify" this UPDATE to table-wide either — budget_id
-- and created_at_utc are immutable BY OMISSION from this list, and column privileges are additive,
-- so a REVOKE cannot take back what a table-wide grant handed out.
REVOKE ALL ON category_groups FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON category_groups TO budgetoid_app;
GRANT UPDATE (name, name_key, description, position) ON category_groups TO budgetoid_app;

-- categories: budget_id and created_at_utc immutable by omission; category_group_id is
-- updatable — moving a category between groups is a real operation, and the composite
-- foreign key to category_groups (id, budget_id) keeps the move inside the budget.
--
-- name_key joins name, the pair the accounts block above argues in full. What this table adds to
-- that argument is a MEASUREMENT of how quiet a half grant is here, taken while the grant really
-- was broken rather than reasoned about afterwards. The category_groups block above predicted the
-- shape; this one is where it was observed end to end, through the route, against a live database:
--
--   update categories set name = ..., name_key = ...                -> 42501
--   update categories set description = ..., name = ..., name_key = ... -> 42501
--   update categories set category_group_id = ...                   -> UPDATE 1
--   update categories set position = ...                            -> UPDATE 1
--
-- PostgreSQL reports 42501 naming the TABLE and nothing else — no column, no constraint, nothing
-- pointing at which entry of this list is missing. aclcheck_error reports the relation, so anybody
-- debugging a broken rename from the message alone gets no clue that name_key is the answer. That
-- is the reason this comment carries the statements rather than a sentence.
--
-- The quiet path is the one to hold on to: a client editing only the DESCRIPTION re-sends the name
-- it already has, every seal draws a fresh nonce, so `name` changes while `name_key` is
-- byte-identical. The blind index's content comparer correctly reports it unchanged, EF names only
-- the columns that moved, and that one-column statement SUCCEEDS under a grant missing name_key.
-- So the hole is invisible on the commonest edit and loud only on a genuine rename — and measured
-- on this suite, the number of genuine category renames that actually reached the database was
-- ZERO until the route bodies were fixed. A broken grant would have shipped green.
REVOKE ALL ON categories FROM budgetoid_app;
GRANT SELECT, INSERT, DELETE ON categories TO budgetoid_app;
GRANT UPDATE (name, name_key, description, position, category_group_id)
    ON categories TO budgetoid_app;

-- payees: no DELETE — no delete path exists. budget_id and created_at_utc immutable by
-- omission.
--
-- name and name_key are the pair the accounts block above argues at length; that argument is not
-- restated here, only pointed at, because it is a rule about blind-indexed name columns generally
-- and a second copy is a second thing to correct the day it changes. This table is the second
-- column to want it, and it wanted it in exactly the live form: Payee.Rename assigns both
-- properties from a single IndexedName, EF emits one UPDATE naming both columns, and a (name)-only
-- list refuses the whole statement with 42501 — renaming a payee would be impossible for this
-- role. That is the defect that shipped on accounts and that no test saw, because the case
-- exercising renames issued a one-column UPDATE and so reported a column privilege while claiming
-- to report an operation.
--
-- SELECT and INSERT are deliberately left table-wide, which is why name_key needed no edit there:
-- PostgreSQL's table-level privilege covers every column, including ones added later. Only the
-- column-list grant had to move. Do not "simplify" this UPDATE to table-wide either — budget_id
-- and created_at_utc are immutable BY OMISSION from this list, and column privileges are additive,
-- so a REVOKE cannot take back what a table-wide grant handed out.
REVOKE ALL ON payees FROM budgetoid_app;
GRANT SELECT, INSERT ON payees TO budgetoid_app;
GRANT UPDATE (name, name_key) ON payees TO budgetoid_app;

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
--   recovery_code_hashes  found by the SHA-256 of the verifier on an ANONYMOUS redemption request,
--                         before anybody has said who they are; a policy keyed on
--                         app.current_user_id would refuse the very query that establishes the
--                         identity, and refuse it loudly — an unset setting reaches the policy as
--                         ''::uuid and raises 22P02
--   session_tokens        found by the SHA-256 of the token a cookie presented, before the request
--                         has said who it is — that lookup IS how the identity is established, so a
--                         policy keyed on app.current_user_id would refuse it, and refuse it on every
--                         authenticated request rather than on one route
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
-- rule. A list of policed tables fails OPEN: the next table nobody added to it keeps the suite
-- green. A list of exemptions fails CLOSED: the next table is red until someone decides. Same names
-- either way, opposite properties.
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
-- passkey_public_keys, recovery_code_hashes and session_tokens carry that same reason, each read
-- before its request has an identity: an assertion names a credential handle and nothing else, a
-- redemption names a verifier and nothing else, a request names a token and nothing else.
-- passkey_public_keys has the sharper illustration beside it — the counter, policed — because that is
-- the same before/after line drawn once more inside a single ceremony, and sessions is the same
-- illustration for session_tokens: the handle is exempt, and everything the handle leads to stays
-- here. Read them together before proposing another exemption: the question is never "is this
-- sensitive" but "is this reachable before the request has an identity", and if the answer is no, a
-- policy costs nothing.

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

-- wrapped_account_keys sits on the same side of the same boundary as passkey_signature_counters, and
-- for the same reason: it is read only after something has already answered who is asking. A passkey
-- assertion has verified, or a recovery code has been redeemed; either way an identity is on the
-- connection before this policy is evaluated. Its sibling passkey_public_keys is read one step
-- earlier, with no identity at all, which is why that table is exempt and this one is not.
--
-- The policy reads only the ownership column, like the two above it. It deliberately says nothing
-- about the credential_type, the factor_id or the envelope: whether a factor may be used at all is
-- decided by the ceremony above this layer, and a predicate here consulting any of those would be
-- inventing an isolation axis beside the two this file carries.
--
-- What this policy does NOT protect against is worth stating, because the absence of a claim is
-- easier to misread than a claim. It scopes which rows the app role may see and write; it cannot look
-- inside either payload, and in particular it cannot tell which half of the 64-byte plaintext inside
-- encapsulated_account_keys is the content key and which is the index key. That ordering is a contract
-- between clients — content key first — checkable only by something holding the private half, which
-- this database has never stored in any form. The database's part is that a row of one account is
-- unreachable from another's session; the rest is cryptographic and deliberately not here.
ALTER TABLE wrapped_account_keys ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS user_isolation ON wrapped_account_keys;
CREATE POLICY user_isolation ON wrapped_account_keys FOR ALL TO budgetoid_app
    USING      (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid)
    WITH CHECK (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid);

-- key_rotations is the staging row of an unfinished content-key rotation: the next generation of the
-- account's factor manifest and the epoch it will be filed at, held beside the generation still in
-- force until one completion step promotes it. It sits on the same side of the same boundary as
-- wrapped_account_keys directly above,
-- for the same reason — a rotation is begun under a passkey assertion that has already verified, so
-- an identity is on the connection before this policy is ever evaluated.
--
-- THE TABLE HOLDS SELECT, INSERT AND A COLUMN-LISTED UPDATE ABOVE, AND STILL NO DELETE. Each of those
-- arrived when something reached for it, which is this file's standing rule: SELECT first, because an
-- ungranted SELECT is the one absence that hides something — the plaintext scan behind
-- NarrativeSecrecyTests meets 42501 and reports the table UNSCANNABLE, so two secrecy gates would pass
-- while covering one table fewer than the schema holds. INSERT and UPDATE followed together with
-- BeginKeyRotationHandler, because staging is an upsert rather than an append and the two cannot be
-- separated. DELETE is still waiting for the completion step. Its own block above carries each of
-- those arguments in full.
--
-- The policy did not wait either, and for a different reason again — the two halves fail in opposite
-- directions, as the header says at length. A table nobody grants is invisible to the role and the
-- first feature to touch it fails loudly with 42501; a table nobody polices is silently readable
-- across every tenant the moment somebody adds the grant. Writing the policy first is the ordering
-- with no silent failure in it, and RlsCoverageTests derives its subject from the live schema, so an
-- absent policy here would fail a test rather than ship. That ordering is what made granting SELECT
-- safe to do early rather than a widening: the policy was already standing when it landed.
--
-- The policy reads only the ownership column, like the three above it. It says nothing about
-- rotation_id and nothing about the staged epoch: whether a chunk may continue a given run is decided
-- by the handler above this layer, and a predicate here consulting either would be inventing an
-- isolation axis beside the two this file carries. What it cannot do is worth stating, because it is
-- the same shape as the sibling's limit: it scopes which rows the app role may see and write, and it
-- cannot tell a well-formed staged manifest from a forged one. That binding is the manifest's own
-- authentication tag, checkable only by a client holding the key.
ALTER TABLE key_rotations ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS user_isolation ON key_rotations;
CREATE POLICY user_isolation ON key_rotations FOR ALL TO budgetoid_app
    USING      (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid)
    WITH CHECK (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid);

-- key_rotation_seals is one surviving factor's copy of the next generation of the account's two keys,
-- encapsulated to that factor's public key and staged beside the run that produced it. It sits on the
-- same side of the same boundary as the two tables above it, and the test is the one the header states:
-- not "is this sensitive" but "is this reachable before the request has an identity". It is not — a run
-- is begun under a passkey assertion that has already verified — so a policy costs nothing and the
-- table is policed rather than exempt.
--
-- THE GRANT ABOVE IS SELECT ALONE AND THE POLICY IS STILL FOR ALL, which is not an oversight and not
-- a widening. factor_manifests was the precedent and is now the worked example rather than the
-- parallel: its policy was written FOR ALL while its grant was SELECT alone, registration arrived and
-- took the INSERT, the promotion path then took a column-listed UPDATE, and the rows each of those
-- statements may touch were already decided. A policy is not a privilege: FOR ALL says which ROWS each
-- command may reach if the role ever holds that command, and holding none of the write commands means
-- the write arms are unreachable today. Writing it narrower would mean the day INSERT is granted — and
-- this table exists to take one — the rows it may write are decided by nobody. The two halves fail in
-- opposite directions, as the header says at length: a table nobody grants fails loudly with 42501, a
-- table nobody polices is silently readable and writable across every tenant. Writing the policy first
-- is the ordering with no silent failure in it.
--
-- The policy reads only the ownership column, like the four above it. It says nothing about factor_id,
-- and it does not need to: that a seal names a factor of this same account is held one layer down by
-- the composite foreign key to wrapped_account_keys(factor_id, user_id), which is a stronger statement
-- than a predicate here could make — it refuses the row outright rather than hiding it. What this
-- policy cannot do is the same shape as its siblings' limit: it scopes which rows the app role may see,
-- and it cannot look inside the payload, so it cannot tell which half of the 64-byte plaintext is the
-- content key and which is the index key. That ordering is a client contract, checkable only by
-- something holding the private half, which this database has never stored.
ALTER TABLE key_rotation_seals ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS user_isolation ON key_rotation_seals;
CREATE POLICY user_isolation ON key_rotation_seals FOR ALL TO budgetoid_app
    USING      (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid)
    WITH CHECK (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid);

-- factor_manifests is the account's list of every recovery factor's public key, one row per account.
-- It sits on the same side of the same boundary as the four tables above it, and the test is the one
-- the header states: not "is this sensitive" but "is this reachable before the request has an
-- identity". It is not — a client asks what to encapsulate to once it already knows whose account it
-- is — so a policy costs nothing and the table is policed rather than exempt.
--
-- THE GRANT ABOVE IS NOW SELECT, INSERT AND A TWO-COLUMN UPDATE, AND THE POLICY WAS FOR ALL BEFORE
-- ANY OF THEM. That ordering is the point, and this table is where it has now paid twice: the policy
-- was written before any write privilege existed, so when registration came to write the first
-- manifest and when the promotion path took its UPDATE, the rows each statement may touch were already
-- decided — by user_isolation's USING for which row an UPDATE may find, and by its WITH CHECK for
-- which owner either statement may leave behind. Had the policy been written narrower, each of those
-- days would have been a day somebody decided a write arm in a hurry. The two halves fail in opposite
-- directions, as the header says at length: a table nobody grants fails loudly with 42501, a table
-- nobody polices is silently readable and writable across every tenant. It is the same ordering
-- key_rotations landed under.
--
-- The UPDATE arm is now reachable and the column list above is what bounds it: WITH CHECK refuses a
-- row leaving under another owner, and user_id is off the grant so no statement can propose one in the
-- first place. The DELETE arm is unreachable for good, because a manifest leaves only by the cascade
-- from users, which runs with the referencing table owner's privileges rather than this role's and is
-- not subject to this policy at all.
--
-- The policy reads only the ownership column, like the five above it. It says nothing about
-- rotation_epoch and nothing about the manifest bytes: whether a generation may be promoted is a
-- question for the handler that will write one, above this layer, and a predicate here consulting the
-- epoch would be inventing an isolation axis beside the two this file carries. What it cannot do is
-- the same shape as its siblings' limit — it scopes which rows the app role may see, and it cannot
-- tell a well-formed manifest from a forged one. That binding is the manifest's own authentication
-- tag, checkable only by a client holding the key, and deliberately not here.
ALTER TABLE factor_manifests ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS user_isolation ON factor_manifests;
CREATE POLICY user_isolation ON factor_manifests FOR ALL TO budgetoid_app
    USING      (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid)
    WITH CHECK (user_id = COALESCE(current_setting('app.current_user_id', true), '')::uuid);
