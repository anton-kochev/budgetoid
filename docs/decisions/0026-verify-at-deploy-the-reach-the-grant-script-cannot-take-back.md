# ADR 0026 — Verify at deploy the reach the grant script cannot take back

- **Status:** Accepted
- **Date:** 2026-09-30
- **Area:** Persistence / Deployment (PostgreSQL roles and grants, deploy-time verification)

## Context

[ADR 0004](0004-connect-as-a-least-privilege-role.md) made `budgetoid_app` hold exactly the
privileges the domain's write surface needs, and leaned on one property to make that safe to change:
a **missing** grant is fail-closed. The first statement that needs it answers `42501`, the feature
breaks loudly, and the fix is a narrow line in `app-role-grants.sql`.

An **extra** grant is the opposite. Nothing raises, every feature works, and the role quietly
reaches something the matrix never gave it. It is the same fail-open shape
[ADR 0006](0006-automate-migrations-and-provisioning-in-the-pipeline.md) found in row-level security,
and it has more ways in:

- **Role attributes.** `ALTER ROLE budgetoid_app BYPASSRLS` switches every isolation policy off for
  the role while each one stays present, enabled and correct in the catalog. An attribute is no
  grant, so no `REVOKE` touches it.
- **Memberships, default privileges, ownership, and anything granted to `PUBLIC`.** None of them is
  a grant *to the role* on a table, so the script's `REVOKE ALL … FROM budgetoid_app` does not reach
  them.
- **Relations outside `public`.** No line of the script names another schema.
- **Grants made by a third role.** A `REVOKE` is performed as the object's owner, even when a
  superuser sends it, and takes back only the entries the owner made. A grant to the role by a third
  role holding a grant option survives the owner's or a superuser's `REVOKE ALL … FROM
  budgetoid_app`, and `REVOKE … GRANTED BY` that role is refused with `0A000` (all three measured on
  postgres:17.10). So a re-run of the script leaves it standing, forever.
- **The database itself.** A new database's ACL is NULL, and PostgreSQL reads a NULL ACL as `PUBLIC`
  holding `CONNECT` and `TEMPORARY` (measured). Every role inherits `PUBLIC`, so the role could make
  temporary tables: a place to put rows no grant, no policy and no census here describes.
  `CREATE DATABASE … TEMPLATE` does not copy the template's ACL (measured), so a tightened template
  would not have helped.

`AppRoleGrantMatrixTests` holds the table and column matrix in CI. It reads a CI database, never
the one a deploy leaves behind, and most of the above is not a table or column grant in `public` at
all.

A deploy-time check had to fit between two arguments this repository already makes, and they point
in opposite directions. `AppRoleGrantMatrixTests` **restates** the matrix rather than parsing it out
of the script, because a test that derives its expectation from the file it checks has its subject
as its own oracle. `VerifyRowLevelSecurityCoverageAsync` **shares** `RowLevelSecurityCoverage` with
`RlsCoverageTests`, because two *executed* lists of what must be policed have no adjudicator when
they disagree, and the one that loses stops noticing a table. A deploy-time copy of the grant
matrix would have to break one of the two.

## Decision

**The deploy refuses any reach the grant script cannot converge away, and leaves the matrix the
script does converge to CI.**

**The script converges the whole of `public`, not only the tables it names.** Before any per-table
block, it revokes all on every table and every sequence in `public` from the role, so a grant on a
table with no block, a view, a materialized view or a sequence is taken back on the next run. The
per-table `REVOKE ALL` lines stay: each one states that block's intent where a reader deciding a
grant is looking. A database block revokes `TEMPORARY` from `PUBLIC` and every database privilege
from the role itself, for the reason under *The database* below.

**`DeploymentDatabaseProvisioning.VerifyAppRoleReachAsync` refuses what a re-run leaves standing.**
`AppRoleReach.DiscoverAsync` reads the catalogs; `AppRoleReach.FindProblems` judges. It refuses:
a missing, non-login or elevated role (`SUPERUSER`, `CREATEROLE`, `CREATEDB`, `REPLICATION`,
`BYPASSRLS`); any membership the role holds (`member` = the role, never `roleid`, because on
PostgreSQL 16 and later the creator is made a member *of* the role automatically, which widens the
creator and not the role); `CREATE` or a grant option on any schema; any default privilege naming
the role, `PUBLIC` or a role it is a member of; anything the role owns; any `PUBLIC` grant on a
relation or column in `public`; any grant the role holds on a relation outside `public`; any
parameter grant; `EXECUTE` on a routine outside the system schemas; `CREATE`, `TEMPORARY` or a grant
option on the current database; and any grant to the role whose recorded grantor is not the object's
owner. Each refusal is one sentence naming the object and the statement that removes it, and
`AppRoleReachException` puts all of them in its `Message`, because a deploy step shows the operator
nothing else. What it does not read — types, languages, large objects, foreign servers, per-role
settings, objects in other databases — is listed in `AppRoleReach`'s remarks, which are the one
place that list lives.

**It runs twice.** `ProvisionAsync` calls it after the coverage check, and `Tools/DbProvision` calls
it again after `AttachAppRoleIdentityAsync`, because that step hands the role to Azure's `pgaadauth`
label provider, which no test here can run. Both calls come **after** the script, never before it:
a widening the script converges away is not reported, because the deploy that removed it is the
report. `ProvisionAsync_WhenTheScriptConvergesAHandIssuedGrant_Succeeds` pins that order.

**The collision is resolved by the shape of the expected answer, not by choosing a side.** The
verifier's expected answer to every rule is a fixed "nothing" — bar `USAGE` without its grant option
on a schema and `CONNECT` without it on the database. It carries no list of tables or columns, so it
is not a second executed copy of the matrix, and it reads nothing out of the script, so it is not its
own oracle. The matrix stays where both arguments already put it: restated in
`AppRoleGrantMatrixTests`, and converged by the script.

**The matrix stays CI-only because a deploy-time check of it could only ever catch the script.** The
script re-converges the role's own grants in `public` on every run, so after it runs, the matrix on
the database is whatever the script says. A deploy check comparing that to a list would be comparing
the script with a second copy of itself. What a re-run cannot converge in `public` — a `PUBLIC`
grant, or a grant by a third grantor — is the verifier's to refuse, not the matrix's. In CI,
`AppRoleGrantMatrixTests` reads the matrix four ways: the written ACLs, effective privileges through
`has_table_privilege` and `has_column_privilege` (which see a predefined role such as
`pg_write_all_data` and ownership, where `aclexplode(relacl)` sees neither), an `UPDATE t SET c =
DEFAULT WHERE false` probe sent as the role at every column of every table in `public`, and the
role's schema privileges.

**The database: `TEMPORARY` leaves `PUBLIC`, `CONNECT` stays there.** NFR-006 says the role cannot
make temporary tables, and the NULL-ACL default says it can, so the script takes `TEMPORARY` from
`PUBLIC`. It takes everything from the role directly as well, because revoking from `PUBLIC` leaves a
direct grant to the role standing. `CONNECT` stays with `PUBLIC` on purpose: revoking it would lock
out every principal that connects by the default — the Entra administrator, the platform's own
roles — and granting it back to each would be a list the script would have to know and keep right.
So the role holds `CONNECT` through `PUBLIC` and holds no database grant of its own, which
`ProvisionAsync_LeavesTheAppRoleExactlyTheDeclaredDatabasePrivileges` pins in both directions.

A `REVOKE` on a database by anyone but its owner is a `WARNING` (`01006`) that changes nothing
(measured, as a non-superuser `CREATEROLE` role that does not own the database). The script carries
on and would report success. The verifier is what turns that silence into a refusal: it reads the
database's effective ACL and refuses `TEMPORARY` wherever the role holds it, naming the database.

**Immutability by omission gains a stated precondition, and a CI census holds it.** ADR 0004's "an
immutable column is one absent from a `GRANT UPDATE` list" is true only while the role's own
`UPDATE` is the only thing that can write a column. Three catalog objects write columns the
statement never named, each with someone else's privileges: a trigger, a rewrite rule, and a foreign
key's referential action. Each was run on postgres:17.10 against a role holding `UPDATE` on one
column, and each wrote something that role's own statement was refused — for the trigger and the
foreign key, a column the probe above still reported refused. `ImmutableColumnRewritePathTests`
refuses all three: no user-defined trigger (disabled and constraint triggers included), no rewrite
rule but a view's own `_RETURN`, and no `ON UPDATE` or `ON DELETE SET NULL`/`SET DEFAULT` action
that the role can set off and that writes a column it cannot `UPDATE`. It scans every schema but the
two system ones, and each arm asserts a floor so a scan that matched nothing cannot pass.

**That census is a CI gate and deliberately not a deploy rule.** A trigger, a rule or a referential
action reaches production by a migration, and CI runs every migration into the database the census
reads; or by an administrator's hand, which is outside the threat model for the reason
`RowLevelSecurityCoverage.FindProblems` gives about a crafted policy — anyone able to create a
trigger on a policed table can `DISABLE ROW LEVEL SECURITY` on it instead, and a deploy gate that
claimed to catch the one would be claiming a guarantee it cannot hold against the other.

**A refused write stays a fault, and a test now says so.** ADR 0004 decided a `42501` surfaces as a
500 and is never translated into a domain error. `PrivilegeRefusalSurfacingTests` holds it on the
accounts `PUT` and the payees `POST`, each of which has a `23505` catch on its path that one widened
filter would turn into a conflict: after a `REVOKE`, each answers the catch-all's 500 with no
`conflictKind` and no `errors`, writes nothing, and logs at Error with SQLSTATE `42501`.

## Alternatives considered

- **Parse `app-role-grants.sql` at deploy and compare the catalog to it.** Rejected: the script has
  just run, so the catalog agrees with it by construction on everything it converges, and the check
  would have the file as its own oracle — the argument `AppRoleGrantMatrixTests` makes against
  parsing, with nothing gained. And a parser of PostgreSQL's `GRANT` grammar is a second
  implementation of something the server already does.
- **Move the matrix into `Infrastructure`, as one list the deploy verifier and the test both
  read.** Rejected: the script is already an executed copy of the matrix, so this makes two —
  the SQL and the C# list — with no adjudicator when they disagree, which is exactly what
  `VerifyRowLevelSecurityCoverageAsync`'s sharing argument forbids. It would also turn the test into
  a reader of the list it is meant to check, and lose the deliberate second writing that makes a new
  grant a conversation.
- **Converge everything in the script, so no verifier is needed.** Rejected as impossible, not as
  costly. Per the PostgreSQL documentation for `ALTER ROLE`, a non-superuser cannot change
  `SUPERUSER`, and can change `REPLICATION` and `BYPASSRLS` only when it holds them itself — so the
  `CREATEROLE` administrator a managed server gives out cannot reset the attributes that matter most
  (not measured here). A grant by a third grantor cannot be taken back by the owner at all. And
  memberships, default privileges of other creators, owned objects, other schemas and parameters
  would each need the script to enumerate objects it cannot name in advance — dynamic SQL in `DO`
  blocks, which is procedural logic pushed down to satisfy "lowest layer", the thing
  [ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md) rules out. Refusing is
  declarative; converging all of that is not.
- **Revoke `CONNECT` from `PUBLIC` and grant it to the role.** Rejected: the effective set is the
  same, and the cost is a list of every other principal that must connect, kept right forever in a
  file about one role.
- **Run the trigger, rule and foreign-key census at deploy too.** Rejected for the reason in the
  Decision: both roads a rewrite path can take are either covered by CI or outside the threat model,
  and claiming more at deploy would be a guarantee the gate cannot hold.

## Consequences

- **A reach refusal fails the deploy after the schema has changed**, as a coverage failure does —
  `ProvisionAsync` migrates, provisions, then verifies. The tool exits `1` and prints each widening
  with the statement that removes it. The fix is to run that statement on the admin connection and
  deploy again; the script cannot do it, which is why it was refused.
- **A refusal after the identity label leaves the role bound and the deploy red.** The label is
  idempotent and the pipeline's `azd deploy` step does not run after a failed one, so the previous
  API keeps serving.
- **Two Azure facts are unmeasured, and the design lets the first deploy find them out.** No
  production environment exists today.
  - [Guessing] Whether the deploying Entra administrator owns the `budgetoid` database on Azure
    Database for PostgreSQL flexible server. If it does not, the `TEMPORARY` revoke is a `WARNING`
    and a no-op, and the verifier refuses the first deploy by design, naming `TEMPORARY` and saying
    the statement must run as the database owner.
  - [Guessing] Whether the `pgaadauth` label gives the role a membership or any other reach. The
    second call after the label exists to find out; if it does, that call refuses.
- **`NonSuperuserDeploymentProvisioningTests` runs the verifier as a non-superuser `CREATEROLE`
  principal that owns the database**, and requires it to accept a clean deploy while the creator's
  automatic membership in the role is present — the one row a rule reading the wrong membership
  column would refuse.
- **A new rule costs a sabotage test.** `DeploymentProvisioningTests` drives one widening at a time
  per rule, against a clean-database control that must pass, so a verifier that refused everything
  would not turn the suite green.
- **A new trigger or rule is red in CI whatever it is for**, and so is a foreign-key action that
  writes a column the role cannot `UPDATE`. The answer is to argue it here first, because each is a
  way around the column lists.
