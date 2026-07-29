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

-- __APP_PASSWORD__ is a token replaced in C# (DatabaseProvisioning.ApplyGrantsAsync) with the
-- password as a single-quoted SQL literal. This mechanism is deliberate: psql-style :'var'
-- interpolation does not exist when the script is executed through Npgsql, and CREATE
-- ROLE/ALTER ROLE cannot take a parameter placeholder. The substitution is safe because the
-- C# side rejects any password outside a conservative ASCII alphabet (no quotes, backslashes,
-- or dollar signs) before substituting — provisioning owns the password, so restricting its
-- alphabet is simpler and stronger than escaping.
DO $provision$
BEGIN
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'budgetoid_app') THEN
        -- A re-run refreshes the password rather than silently keeping an old one.
        ALTER ROLE budgetoid_app WITH LOGIN PASSWORD __APP_PASSWORD__;
    ELSE
        CREATE ROLE budgetoid_app WITH LOGIN PASSWORD __APP_PASSWORD__;
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
