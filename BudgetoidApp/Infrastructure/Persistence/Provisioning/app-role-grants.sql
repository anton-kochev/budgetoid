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

-- users: google_subject is the key the whole sign-in resolves through, and created_at_utc is
-- an audit fact — both immutable by omission from the UPDATE list.
REVOKE ALL ON users FROM budgetoid_app;
GRANT SELECT, INSERT ON users TO budgetoid_app;
GRANT UPDATE (email, display_name) ON users TO budgetoid_app;

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
-- Exactly the five budget-owned tables are policed, mirroring the BudgetIsolation query filters.
-- budgets, users and currencies are out because a budget is the tenant rather than a tenant's row
-- and the other two belong to no tenant — and provisioning reads users and budgets before an
-- ambient budget exists, so a policy on either would break sign-in.
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

-- Every session of the role starts with the setting DEFINED and empty. Without this default,
-- strict current_setting has a nondeterministic SQLSTATE for the same bug: 42704 ("unrecognized
-- configuration parameter") on a backend that has never seen the setting, but 22P02 once
-- set_config has run and Npgsql's pool reset has left the parameter defined as ''. Making the
-- unset state always the ''::uuid cast is what lets a session that names no budget fail the same
-- way on a fresh connection and a recycled one alike. RlsIsolationTests pins that code.
ALTER ROLE budgetoid_app SET app.current_budget_id = '';

ALTER TABLE accounts ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS budget_isolation ON accounts;
CREATE POLICY budget_isolation ON accounts FOR ALL TO budgetoid_app
    USING      (budget_id = current_setting('app.current_budget_id')::uuid)
    WITH CHECK (budget_id = current_setting('app.current_budget_id')::uuid);

ALTER TABLE category_groups ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS budget_isolation ON category_groups;
CREATE POLICY budget_isolation ON category_groups FOR ALL TO budgetoid_app
    USING      (budget_id = current_setting('app.current_budget_id')::uuid)
    WITH CHECK (budget_id = current_setting('app.current_budget_id')::uuid);

ALTER TABLE categories ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS budget_isolation ON categories;
CREATE POLICY budget_isolation ON categories FOR ALL TO budgetoid_app
    USING      (budget_id = current_setting('app.current_budget_id')::uuid)
    WITH CHECK (budget_id = current_setting('app.current_budget_id')::uuid);

ALTER TABLE payees ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS budget_isolation ON payees;
CREATE POLICY budget_isolation ON payees FOR ALL TO budgetoid_app
    USING      (budget_id = current_setting('app.current_budget_id')::uuid)
    WITH CHECK (budget_id = current_setting('app.current_budget_id')::uuid);

ALTER TABLE transactions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS budget_isolation ON transactions;
CREATE POLICY budget_isolation ON transactions FOR ALL TO budgetoid_app
    USING      (budget_id = current_setting('app.current_budget_id')::uuid)
    WITH CHECK (budget_id = current_setting('app.current_budget_id')::uuid);
