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
- **Relations outside `public`, and the system catalogs.** No line of the script names another
  schema. A grant to the role on `pg_catalog.pg_statistic`, on the `rolpassword` column of
  `pg_authid` or on `pg_read_file` let it read sampled values of another tenant's rows past
  row-level security, the roles' password hashes, and the server's files (measured).
- **Stored session defaults.** A `pg_db_role_setting` row is applied to the role's sessions before
  the API sends a statement, with no grant on the parameter. `session_replication_role = replica`
  stored that way switched foreign-key enforcement off for the application's sessions (measured).
- **Grants made by a third role.** A `REVOKE` takes back only the entries recorded against its own
  grantor. Sent by the owner, a superuser or a member inheriting the owner, it is recorded as the
  owner's; sent through a role holding a grant option, as that holder's. A grant to the role by a
  third role holding a grant option survives the owner's or a superuser's `REVOKE ALL … FROM
  budgetoid_app`, and `REVOKE … GRANTED BY` that role is refused with `0A000` (all three measured on
  postgres:17.10). So a re-run of the script leaves it standing, forever.
- **The database itself.** A new database's ACL is NULL, and PostgreSQL reads a NULL ACL as `PUBLIC`
  holding `CONNECT` and `TEMPORARY` (measured). Every role inherits `PUBLIC`, so the role could make
  temporary tables: a place to put rows no grant and no policy here describes.
  `CREATE DATABASE … TEMPLATE` does not copy the template's ACL (measured), so a tightened template
  would not have helped.
- **Writes the role's own `UPDATE` never named.** ADR 0004's "an immutable column is one absent from
  a `GRANT UPDATE` list" is true only while the role's own `UPDATE` is the only thing that can write
  a column. A trigger, a rewrite rule and a foreign key's referential action each write columns the
  statement never named, with someone else's privileges, and a stored generated column is recomputed
  with nobody's privilege asked. Each was run on postgres:17.10 as the role and wrote a column or a
  row the role's own statement is refused on — for the trigger and the foreign key, a column the
  `SET c = DEFAULT` probe below still reported refused.

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

**The deploy refuses the reach the grant script cannot converge away, over the catalogs
`AppRoleReach` reads, and leaves the matrix the script does converge to CI.**

**The script converges the whole of `public`, not only the tables it names.** Before any per-table
block, it revokes all on every table and every sequence in `public` from the role, so a grant on a
table with no block, a view, a materialized view or a sequence is taken back on the next run. The
per-table `REVOKE ALL` lines stay: each one states that block's intent where a reader deciding a
grant is looking. A database block revokes `TEMPORARY` from `PUBLIC` and every database privilege
from the role itself, for the reason under *The database* below.

**`DeploymentDatabaseProvisioning.VerifyAppRoleReachAsync` refuses what a re-run leaves standing.**
`AppRoleReach.DiscoverAsync` reads the catalogs; `AppRoleReach.FindProblems` judges. It refuses:

- a missing, non-login or elevated role (`SUPERUSER`, `CREATEROLE`, `CREATEDB`, `REPLICATION`,
  `BYPASSRLS`);
- a membership the role holds — `member` = the role, never `roleid`, because on PostgreSQL 16 and
  later a **non-superuser** creator is made a member *of* the role automatically, which widens the
  creator and not the role (a superuser creator gets no such row, measured);
- `CREATE` or a grant option on a schema; a default privilege naming the role, `PUBLIC` or a role it
  is a member of; an object the role owns, in this database or cluster-wide;
- a `PUBLIC` grant on a relation or column in `public`; a grant the role holds on a relation or
  column outside `public` and the two system schemas; a grant to the role itself, by name, on a
  relation, column or routine in `pg_catalog` or `information_schema`;
- a parameter grant; `EXECUTE` on a routine outside the system schemas; `CREATE`, `TEMPORARY` or a
  grant option on the current database; `CREATE` or a grant option on a tablespace;
- a stored session default: a `pg_db_role_setting` row for the role in any database, and a row for
  every role on the current one;
- a user-defined trigger in any schema, disabled and constraint triggers included; a rewrite rule
  other than a view's own `_RETURN` outside the system schemas;
- a column a referential action writes that the role cannot `UPDATE`, where the role can set the
  action off directly or through a chain of cascades — the walk is recursive, so a delete the role
  holds that cascades into a table it cannot touch and nulls a key one hop further is found; and a
  generated column the role cannot `UPDATE`, on a table where the role can write a column, directly
  or through such an action;
- a grant to the role whose recorded grantor is not the object's owner, with one exception on a
  schema, below.

What it does not read is listed in `AppRoleReach`'s remarks, and this record does not restate it.
`AppRoleReachException` puts every refusal in its `Message`, one per line, under a lead sentence
that holds for a missing or non-login role as much as for a widening, because a deploy step shows
the operator nothing else. Each line names the object and the statement that clears it.

**The grantor rule accepts one grant it would otherwise refuse, on a schema only.** When the
verifying principal does not inherit the schema's owner, a grant whose grantor is the single
grant-option holder that principal inherits is accepted as the principal's own. That is the shape
where the deploy principal owns nothing and reaches schema `public` through a role holding the grant
option — on Azure, [Guessing] the part `azure_pg_admin` plays. There the script's
`GRANT USAGE ON SCHEMA public` is recorded with the holder as grantor, and the principal's own
`REVOKE` takes it back (measured). With two such holders, which one PostgreSQL records is not the
principal's to decide, so the rule refuses; a holder reached only through a `SET`-only membership is
refused too, because such a member's own `GRANT` records nothing.
`ProvisionAsync_AsAPrincipalInheritingTheOneSchemaGrantOptionHolder_ProvisionsTheDatabase`,
`ProvisionAsync_AsAPrincipalInheritingTwoSchemaGrantOptionHolders_RefusesTheDeploy` and
`VerifyAppRoleReachAsync_GrantMadeThroughASetOnlyMembership_ThrowsNamingTheGrantor` hold the three
cases. Because the exception asks who `current_user` inherits, **the verifier must connect as the
principal that ran the script**; read as anybody else, the same catalog can answer differently.

**It runs twice, both times as that principal.** `ProvisionAsync` calls it after the coverage check,
and `Tools/DbProvision` calls it again after `AttachAppRoleIdentityAsync`, because that step hands
the role to Azure's `pgaadauth` label provider, which no test here can run. Both pass the admin
connection string they provisioned with. Both calls come **after** the script, never before it:
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

A `REVOKE` on a database sent by anyone but its owner changes nothing, and the script carries on and
reports success either way (measured on postgres:17.10 and 18.3). Sent by a role holding no grant
option there, it is a `WARNING` (`01006`). Sent through an inherited grant-option holder, it is
performed as that holder, takes back only that holder's entries, and says nothing at all. The
verifier is what turns both into a refusal: it reads the database's effective ACL and refuses
`TEMPORARY` wherever the role holds it, naming the database and saying the statement must run as its
owner.

Revoking `TEMPORARY` does not close every place the role could put rows: `lo_from_bytea` still
stored a large object after it (measured). The object is the role's, and the ownership rule refuses
it at the next deploy. The database rule reads the current database only. The role still holds
`CONNECT` and `TEMPORARY` on the cluster's other databases through `PUBLIC`, and nothing refuses
that; it is not checked.

**Immutability by omission gains a stated precondition, and the deploy holds it.** The trigger,
rule, referential-action and generated-column rules above are that precondition, read against the
live database on every deploy. Each rule has sabotage tests in `DeploymentProvisioningTests`, and
the refusals that need one have a control the verifier must accept:
`VerifyAppRoleReachAsync_ViewInPublic_DoesNotThrow` for the `_RETURN` exclusion, and
`VerifyAppRoleReachAsync_ReferentialActionTheRoleCannotSetOff_DoesNotThrow` for an action that
writes a refused column but that nothing the role can do fires. The CI-only alternative is rejected below.

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
  costly. A non-superuser `CREATEROLE` administrator can reset `CREATEROLE` on the role, and
  `CREATEDB`, `REPLICATION` and `BYPASSRLS` when it holds that attribute itself; no
  non-superuser can alter a `SUPERUSER` role at all (measured on postgres:17.10 and 18). So what the
  script could reset depends on what the deploying principal holds, which this repository does not
  control. The same administrator's `RESET` of a superuser-only session default answers `42501`, and
  `RESET ALL` succeeds while keeping that setting (measured). A grant by a third grantor cannot be
  taken back by the owner at all. And memberships, default privileges of other creators, owned
  objects, other schemas, parameters, triggers and rules would each need the script to enumerate
  objects it cannot name in advance — dynamic SQL in `DO` blocks, which is procedural logic pushed
  down to satisfy "lowest layer", the thing
  [ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md) rules out. Refusing is declarative;
  converging all of that is not.
- **Revoke `CONNECT` from `PUBLIC` and grant it to the role.** Rejected: the effective set is the
  same, and the cost is a list of every other principal that must connect, kept right forever in a
  file about one role.
- **Hold the trigger, rule and foreign-key census in CI only.** Rejected. It was argued that a hand
  edit is outside the threat model, because anyone able to create a trigger on a policed table could
  disable row-level security on it instead. That was no reason: the deploy already refuses a table
  with row security switched off, which
  `VerifyRowLevelSecurityCoverageAsync_RowSecurityDisabled_ThrowsListingTheTable` holds, so that
  road was never open. The verifier's threat is drift and accident — a statement run by hand and
  never undone — not an administrator working against it, and that is what a CI gate cannot see:
  CI only ever read CI's database, never the one a deploy leaves behind. The rules are catalog
  reads with a fixed "nothing" as the answer, the same shape as the other rules here, so moving
  them cost no second copy of anything.

## Consequences

- **A reach refusal fails the deploy after the schema has changed**, as a coverage failure does —
  `ProvisionAsync` migrates, provisions, then verifies. The tool exits `1` and prints each finding
  with the statement that clears it. The fix is to run that statement as the principal it names and
  deploy again; the script cannot do it, which is why it was refused.
- **A refusal after the identity label leaves the role bound and the deploy red.** The label is
  idempotent and the pipeline's `azd deploy` step does not run after a failed one, so the previous
  API keeps serving.
- **Azure facts are unmeasured, and the design lets the first deploy find them out.** No production
  environment exists today.
  - [Guessing] Whether the deploying Entra administrator owns the `budgetoid` database on Azure
    Database for PostgreSQL flexible server, or inherits a grant-option holder that does not. If it
    owns nothing, the `TEMPORARY` revoke changes nothing — with a `WARNING` when it holds no grant
    option, silently when it inherits one — and the verifier refuses the first deploy by design,
    naming `TEMPORARY` and saying the statement must run as the database owner.
  - [Guessing] Whether Azure's administrator shape lets the migration run at all. An administrator
    whose membership in the holder is `INHERIT FALSE`, or one without `CREATE` on schema `public`,
    fails the migration with `42501` before either verifier runs (both measured). The owner check
    `DEPLOYMENT.md` gives therefore asks for membership **with inherit**, not membership alone.
  - [Guessing] Whether the `pgaadauth` label gives the role a membership or any other reach. The
    second call after the label exists to find out; if it does, that call refuses.
- **`NonSuperuserDeploymentProvisioningTests` runs the deploy as non-superuser principals.** A
  `CREATEROLE` principal that owns the database must see a clean deploy accepted while the creator's
  automatic membership in the role is present — the one row a rule reading the wrong membership
  column would refuse. A principal that owns nothing and inherits one grant-option holder must be
  accepted too, which is the shape the schema exception exists for; the two-holder and `SET`-only
  shapes beside it must be refused.
- **A new rule costs a sabotage test.** `DeploymentProvisioningTests` drives one widening at a time
  per rule, against a clean-database control that must pass, so a verifier that refused everything
  would not turn the suite green.
- **A new trigger or rule is refused at deploy whatever it is for**, and so is a foreign-key action
  or a generated column that writes a column the role cannot `UPDATE`. One added by a migration
  turns `VerifyAppRoleReachAsync_OnAFreshlyProvisionedDatabase_DoesNotThrow` red in CI first, because that
  control runs every migration before the verifier. The answer is to argue it here first, because
  each is a way around the column lists.
