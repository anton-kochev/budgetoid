# ADR 0007 — Authenticate to PostgreSQL with Microsoft Entra managed identities

- **Status:** Accepted
- **Date:** 2026-07-30
- **Area:** Deployment / Security (Microsoft Entra authentication, Azure Database for PostgreSQL
  Flexible Server, managed identity, PostgreSQL roles)

## Context

[ADR 0001](0001-postgres-password-authentication.md) chose password authentication and wrote down the
way back: a token provider in the API, an explicit username and SSL mode on the connection string,
and `passwordAuth: Disabled` on the server. [ADR 0004](0004-connect-as-a-least-privilege-role.md)
then split the identity the application authorizes as away from the administrator, and
[ADR 0006](0006-automate-migrations-and-provisioning-in-the-pipeline.md) moved migration and
provisioning into the pipeline. Each of those left the same residue: a standing database password.
Three of them, in fact — the server administrator's in Key Vault, the application role's as a GitHub
secret baked into a Container App secret, and a literal in the AppHost for local development. Two of
the three are production credentials that exist whether or not anybody is deploying, can be copied
without being used, and are revoked only by someone remembering to rotate them.

The mechanism that removes them is easiest to state without jargon. A password is a house key: cut
once, handed out, valid until the lock is changed, and just as useful to whoever finds it on the
floor. Microsoft Entra token authentication is the hotel front desk: the API walks up as itself,
the desk cuts a keycard good for less than an hour, and the card is re-cut on the next visit. There
is nothing to store, because nothing issued outlives the visit; there is nothing to leak from the
container's environment, because the environment holds no card. PostgreSQL is unchanged by any of
this — the token arrives in the password field of the startup packet, and to the server a token *is*
the password. What changes is where the value comes from and how long it is worth anything.

The obstacle ADR 0001 hit was never the server. Entra-only auth was already configured correctly
there. The failure was the **client** connection string: Aspire emitted bare `Host=…;Database=…`, so
Npgsql defaulted the username to the container's OS user — `app`, in the `noble-chiseled` image —
and connected without TLS, which Azure refused with `28000`. Both halves of that are addressable
without custom Npgsql code: Aspire's *Azure* Npgsql client integration supplies the token provider,
and the username is written explicitly instead of being inferred.

## What this supersedes

Per this repository's convention an ADR is amended by a later ADR and never edited, so the sentences
that no longer describe the system are named here rather than corrected in place.

- **[ADR 0001](0001-postgres-password-authentication.md) — Decision superseded, "Revisit later"
  executed.** Its three-step hardening path is what this record implements, and its Decision
  ("Switch the production database connection to password authentication", built by
  `.WithPasswordAuthentication()` with migrations on the same admin credentials) no longer describes
  anything. The reason its original attempt failed does not recur, and the reason is specific rather
  than reassuring: the gap was a client connection string with no username and no SSL mode, and it is
  closed by naming `budgetoid_app` explicitly in the AppHost plus the Azure client integration's
  token provider. Gotcha #2 — the connection string carries no `SslMode`, and Azure rejects
  unencrypted connections — **still stands**, and is still implemented in `BudgetoidApp/Api/Program.cs`
  by rebuilding the string with `SslMode=Require` outside Development. Gotcha #1's root fix also
  stands: the connection string is built explicitly in the AppHost rather than referenced out of Key
  Vault.
- **[ADR 0004](0004-connect-as-a-least-privilege-role.md) — Decision stands, two credential
  paragraphs superseded.** Everything about *authorization* holds unchanged, including the part this
  record depends on entirely: immutability expressed by omission from a `GRANT UPDATE` column list,
  fail-closed grants, and a script that converges rather than a migration that applies once.
  Superseded only where it describes the role as having a password that provisioning assigns — "the
  role's password is read out of the application connection string, not from a key of its own" — and
  where the password alphabet is described as a deploy-time concern the operator must respect, and
  where its Consequences say the two database identities are rotatable credentials. Password
  provisioning is a development-and-test path; production has no application role password to assign,
  validate or rotate.
- **[ADR 0006](0006-automate-migrations-and-provisioning-in-the-pipeline.md) — ordering stands, Key
  Vault and password validation superseded.** Its migrate-then-provision-then-verify ordering inside
  one method is untouched and is the reason this record could add a fourth step without adding a
  second mechanism. Superseded where it says "the pipeline reads the admin connection string from
  Key Vault", where it justifies the pipeline identity holding **Key Vault Secrets User** (its own
  Consequences predicted this removal), and where it argues for validating the application role
  password before the first statement — there is no such password in the pipeline, and
  `ProvisionAsync_InvalidPasswordAlphabet_ThrowsBeforeTouchingTheDatabase`, the test that pinned that
  ordering, is gone with it. `EnsureValidAppRolePassword` remains public and still guards the
  development path.
- **[ADR 0005](0005-isolate-budget-owned-rows-with-row-level-security.md) — untouched, and a reader
  will wonder why.** Every policy is written `TO budgetoid_app`, and how a role proves it is that
  role is invisible to a policy. The role keeps its name, its grants, its ownership and its
  memberships; only the credential attached to it changes. Nothing in `app-role-grants.sql` needed
  editing for this decision, which is the strongest evidence that ADR 0004's role split was the right
  seam.

## Decision

**Production authenticates with Microsoft Entra tokens from managed identities, and the server
accepts nothing else.** The generated Bicep carries `activeDirectoryAuth: Enabled` with
`passwordAuth: Disabled`, which is Aspire's default for this resource and is left in place simply by
not calling `WithPasswordAuthentication`. There is no database password anywhere in production: not
in a Container App secret, not in Key Vault, not in a GitHub secret. A credential that does not
exist cannot be leaked, mis-scoped, or found in a log.

**Two identities, and the split is the point.** The API runs as its user-assigned managed identity
and connects as `budgetoid_app`, the least-privilege role ADR 0004 created. The deploy pipeline's
service principal is a Microsoft Entra **administrator** of the server and does everything that
needs privilege: migrate, apply grants and policies, verify coverage, bind the role to the API's
identity. Nothing in the request path can do any of that, and nothing in the deploy path serves a
request.

**The absence of `Password=` is what activates the token provider.** The API's connection string is
`Host={postgres.outputs.hostName};Username=budgetoid_app;Database=budgetoid`, and
`builder.EnrichAzureNpgsqlDbContext<BudgetoidDbContext>()` in `BudgetoidApp/Api/Program.cs` layers a
password provider onto the data source that fetches a token from the container's identity per
physical connection open (Aspire reads `AZURE_CLIENT_ID` / `AZURE_TOKEN_CREDENTIALS`, which its
Container Apps publisher injects). The same integration **stands aside entirely when the connection
string already carries a username and a password**, which is exactly the local-dev and
integration-test case. That is why there is no environment check in the registration: one call serves
every environment and the connection string decides. The username is written explicitly rather than
inferred, because the integration can only derive a username from token claims and no claim ever
spells a custom role name.

**The pipeline's service principal is registered as an Entra administrator, not merely a privileged
role.** `PostgreSqlFlexibleServerActiveDirectoryAdministrator` is added explicitly in
`BudgetoidApp/AppHost/Program.cs` from the `pipeline-principal-id` and `pipeline-principal-name`
parameters, so nothing about the pipeline's identity is checked into the repository. Administrator is
the requirement rather than the convenience: only an Entra administrator can create or label Entra
principals in the database, and membership in `azure_pg_admin` alone does not confer that. The
resource's *name* is the object id and the display name is only a property, for the reason the next
paragraph gives.

**Azure matches a token to a database role by the principal's object id, never by name.** So the role
keeps the name every grant and every policy already spells, and gains a security label carrying the
identity's object id. `DatabaseProvisioning.BuildAppRoleIdentitySql` emits exactly two statements:

```sql
SECURITY LABEL for "pgaadauth" on role budgetoid_app is 'aadauth,oid=<oid>,type=service';
ALTER ROLE budgetoid_app WITH PASSWORD NULL;
```

A label attaches to the role's **existing** identity, so grants, ownership and role memberships all
survive it — which is why nothing in `app-role-grants.sql` and none of ADR 0005's policies changed.
The order is load-bearing rather than tidy: nulling the password before the label is attached leaves
a window in which the role has no credential of either kind, and on a re-provision that window is a
production API that cannot log in. `type=service` because a managed identity is a service principal;
`type=user` sends Azure looking for the object id in the wrong directory object class. The object id
is spliced into the literal because the `SECURITY LABEL` grammar takes no bound parameter, and typing
the parameter as `Guid` is the whole defence — a `Guid` renders as hex and hyphens and can no more
contain a quote than a semicolon. The SQL text is built in a separate public method so that it can be
pinned by a unit test, because it cannot be *executed* anywhere but Azure.

**The label can only be applied over an Entra-authenticated admin connection to the `postgres`
database**, which is where Azure exposes the `pgaadauth` label provider. `AttachAppRoleIdentityAsync`
therefore rewrites the supplied connection string's `Database` through
`NpgsqlConnectionStringBuilder` and preserves every other option. A hardcoded string would lose the
TLS and timeout settings the admin connection needs; leaving `Database` alone would miss the provider.

**`app-role-grants.sql` contains no credential of any kind, and that is a structural claim rather
than a tidiness one.** The role is created `WITH LOGIN` and nothing else. *What* the role may do lives
in the script; *how it proves who it is* is attached afterwards by whichever environment-specific
path applies — `AttachAppRoleIdentityAsync` in production, `AttachAppRolePasswordAsync` locally and in
tests. One consequence is worth stating rather than discovering: **a re-run of the grants script can
no longer reset a credential the environment owns**, because provisioning does not know one. `LOGIN`
stays in the script because it is a property of the role's purpose rather than of its credential, and
`LOGIN` without a password and without a label authenticates nothing.

**Identity binding sits outside `ProvisionAsync`, deliberately, unlike migrate → grants → verify.**
`Tools/DbProvision` calls `ProvisionAsync` and then `AttachAppRoleIdentityAsync`, strictly after,
because the role must exist before it can be labelled. It is not folded into the method because
ordering guarantees are spent where silence is possible: forgetting the binding is the loudest
failure the system has — the application cannot authenticate at all, `28P01`, on every request —
whereas the row-level security coverage `ProvisionAsync` verifies fails **open** and reports nothing.
The one that cannot be missed needs no machinery to make it unmissable.

**The pipeline mints its own token in the step that uses it.**
`az account get-access-token --resource-type oss-rdbms` produces the value,
`::add-mask::` hides it immediately, and it goes into
`DBPROVISION_ADMIN_CONNECTION_STRING` as the `Password=` component alongside the administrator's
principal name as `Username=` — the token proves *which* principal, but PostgreSQL still needs a role
name to log in as. It is never written to `GITHUB_ENV`, for ADR 0006's reason made sharper: that file
is inherited by every later step, and a live admin token in it would outlive the one step that needs
it. `DbProvision` still takes plain strings and references nothing but `Infrastructure`, so no Azure
SDK and no credential chain enter that dependency graph.

**`ClearDefaultRoleAssignments()` is required on the server resource, and this is the least obvious
fact in the record.** Dropping `WithPasswordAuthentication` also drops its side effect of clearing
Aspire's default role assignments, so Aspire emits a `postgres-roles` Bicep module whose
administrators resource takes its name from a principal id parameter that nothing fills — no compute
resource references this server, deliberately — and ARM refuses a resource with an empty name, so
`azd provision` fails outright before anything is deployed. `ClearDefaultRoleAssignments()` is the
supported opt-out. Recorded here because someone re-deriving the AppHost will hit it and will not
connect it to the call they removed.

**The API still does not `WithReference` the database, and under Entra auth that matters more than it
did.** For this resource type a reference registers the referencing compute resource's managed
identity as a full Entra **administrator** of the server: `azure_pg_admin`, `CREATEROLE`, `CREATEDB`.
An administrator is not subject to row-level security policies, so the entire isolation design of ADR
0005 would become decoration — not weakened, bypassed — and the column-list grants of ADR 0004 with
it. ADR 0004 recorded the missing reference because it looked like an omission someone would "fix"
back in. That warning is renewed with a worse consequence attached.

## Alternatives considered

- **Keep passwords and rotate them on a schedule.** Rejected, and it is the honest baseline to
  compare against. Rotation reduces the window in which a leaked value works; it does not remove the
  value, the two places it has to agree, or the deploy-time window ADR 0006 documented in which a
  cold-start replica presents a new password to a role that still has the old one. A token that lives
  under an hour and is never stored achieves the goal of rotation continuously and needs nobody to
  remember it.
- **Let the API connect as its own managed identity via `WithReference`, and skip the label
  entirely.** Rejected, and it is the alternative that looks most like the intended design. The
  reference is the supported wiring, it produces a working connection with no provisioning step, and
  it makes the request-serving process an Entra administrator of the server — which silently disables
  every policy in ADR 0005 and every column restriction in ADR 0004, because PostgreSQL skips both
  for a superuser-equivalent. Nothing would fail. That is what makes it dangerous rather than merely
  wrong.
- **Create a *new* Entra-principal role named after the managed identity and grant it everything
  `budgetoid_app` has.** Rejected: it duplicates the grant matrix and the five policies onto a second
  role name, or rewrites both to name the new one, and the migration between the two states is the
  window in which one of them is wrong. Labelling the existing role changes the credential and
  nothing else, and the diff proves it — no line of `app-role-grants.sql` moved.
- **Use `pgaadauth_create_principal_with_oid(...)` as the primary mechanism.** Rejected as the primary
  and kept as the break-glass. It is a procedural helper whose behaviour against a role that already
  exists, already holds grants and is already the subject of policies is not something we can verify
  anywhere but on the live server; the `SECURITY LABEL` form is declarative, states exactly what it
  changes, and is the thing the helper is documented as wrapping. See the open risk below — this
  choice is the one place in the record where the safer-looking option is the fallback rather than the
  default.
- **A two-phase cutover with both auth modes enabled, then a second deploy to disable passwords.**
  Rejected. Azure supports both auth modes simultaneously, so this is available in principle, but
  Aspire exposes no dual-mode API: the AppHost would need hand-written Bicep customization and the API
  a connection string carrying a password that the Azure enrichment must then be persuaded to ignore.
  All of that would be written in order to be deleted a day later, and the machinery itself is more
  likely to leave a mistake behind than the few minutes of 500s it avoids.
- **Fold identity binding into `ProvisionAsync` for symmetry with migrate → grants → verify.**
  Rejected: symmetry is not the criterion, silence is. The three steps inside `ProvisionAsync` are
  there because one of them fails open and the ordering between them cannot be trusted to a workflow
  file. An unbound identity announces itself as `28P01` on the first request, which no guarantee can
  improve on.
- **Let `DbProvision` acquire its own token with `DefaultAzureCredential`.** Rejected for exactly the
  reason ADR 0006 rejected letting it read Key Vault: it pulls the Azure SDK and a credential chain
  into `Infrastructure`'s dependency graph to save one `az` invocation, and makes the tool untestable
  against a bare container without stubbing a cloud. Plain strings in, no ambient identity.
- **Move local development and the integration tests to Entra auth as well, for parity.** Rejected:
  the only ways to do it are a real Azure dependency inside the test suite or an environment branch in
  production code, and the connection-string-decides behaviour of the Azure enrichment gives parity
  where it is cheap — one registration, two credential shapes — without either.

## Consequences

- **Cutover is a single deploy with a service window, and the window is accepted rather than
  overlooked.** `azd provision` flips the server's authentication mode, which restarts the server,
  while the running revision still expects a password. The API returns 500s from that moment until
  `azd deploy` lands the token-authenticating revision a few minutes later. The alternative was the
  dual-auth rollout rejected above; this is the deliberate trade.
- **The pipeline identity's Key Vault Secrets User assignment is no longer needed**, and the vault
  Aspire provisioned for the administrator password is no longer generated. Bicep's incremental mode
  never deletes anything, so the existing vault and its stale secret linger until someone removes
  them by hand. Recorded because a vault holding a working administrator password for a server that
  refuses password authentication is a confusing artifact to find, not a dangerous one.
- **Open risk, `[UNCONFIRMED]`: the non-admin label form has never been executed.**
  `'aadauth,oid=…,type=service'` without a trailing `,admin` is inferred from the label grammar; the
  vendor documentation only shows the admin-bearing form. Vanilla PostgreSQL has no `pgaadauth` label
  provider, so no container test can run the real statement — the unit test pins the emitted text and
  nothing more. **The first Entra deploy is the first execution of that statement, ever.** Break-glass
  if it is refused: connect as the Entra administrator to the `postgres` database and run
  `pgaadauth_create_principal_with_oid('budgetoid_app', <oid>, 'service', false, false)` instead. The
  role is never dropped on either path, so its grants, its policies and its ownership survive the
  experiment, and a failed attempt costs a re-run rather than a rebuild.
- **Token lifetime is under an hour, and the one thing we do not know is stated as unknown.** Every
  new physical connection fetches a fresh token, and Npgsql prunes idle connections well inside that
  window, so the ordinary case is covered by construction. Whether an already-open connection that
  outlives its token is torn down by Azure, or continues indefinitely, is undocumented and untested by
  us. If long-lived connections start failing mid-life, that is the first thing to measure — asserting
  either behaviour here would be inventing a fact.
- **Local development and the integration tests keep password authentication, and that is not a
  gap.** Both run against a PostgreSQL container reachable only from the machine it runs on, whose
  superuser credentials are already a fixed local default; the password the dev role gets is a literal
  in the AppHost inside the alphabet `DatabaseProvisioning` allows. What password auth buys there is
  the absence of an Azure dependency in the test suite and the absence of an environment branch in
  production code — the enrichment self-disables on a connection string that carries a password, so
  the *same* registration is exercised locally and in production.
- **Everything the deploy needs is an identifier, and identifiers are repository variables.**
  `AZURE_PIPELINE_PRINCIPAL_ID` and `AZURE_PIPELINE_PRINCIPAL_NAME` are `vars`, not `secrets`, because
  an object id and a display name authenticate nothing without the federated OIDC login. A guard step
  fails the run when either is empty, because an unset variable resolves to the empty string and ARM
  would reject the administrators resource with a message about the resource rather than about the
  missing input.
- **The API's identity object id is read off the deployed container app rather than derived from a
  naming convention.** `az containerapp show --query "identity.userAssignedIdentities.*.principalId"`
  is the authority on which principal the API actually runs as, and the step fails loudly when it
  resolves to nothing. A wrong object id produces a runtime login failure that looks nothing like a
  provisioning bug, which is the diagnosis this avoids.
- **`DbProvision` prints the exception message and never a stack trace.** An Npgsql frame can carry
  the admin connection string, and that string is a live access token; a trace in build output would
  outlive the deploy that produced it.
