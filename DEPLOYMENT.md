# Deploying Budgetoid to Azure

Budgetoid is a **production service**. This baseline starts at the cheapest production-viable
tier (real backups + security, minimal spend) with a clear path to scale up on load.

| Component | Host (start) | Scale-up lever |
|---|---|---|
| API (.NET 10) | Azure Container Apps, consumption, scale-to-zero (`MinReplicas = 0`) | → `MinReplicas = 1` when cold starts bite |
| Frontend (Angular) | Azure Static Web Apps, **Free** (custom domain + TLS included) | → Standard for the SLA |
| PostgreSQL | Azure Postgres **Flexible Burstable B1ms**, 32 GB, 7-day backups + PITR | → General Purpose → zone-redundant HA |
| DB auth | **Microsoft Entra only** — no database password exists. The API authenticates as its managed identity and connects as the least-privilege `budgetoid_app` role; the deploy pipeline authenticates as an Entra administrator to migrate and provision (see [ADR 0007](docs/decisions/0007-authenticate-to-postgres-with-managed-identity.md)) | — |

**Infra is code.** The Aspire `AppHost` is the source of truth; `azd` provisions **the API and the
PostgreSQL server** from it, regenerating the Bicep at deploy time (the synthesized `./infra` is
gitignored, not committed, to avoid drift). The **Static Web App is the only out-of-band resource** —
created once with one command.

> **How the database connection works.** Nothing holds a database password. The API's connection
> string is `Host=…;Username=budgetoid_app;Database=budgetoid`, and the missing `Password=` is what
> makes Aspire's Azure Npgsql integration fetch an access token from the container's managed
> identity instead. Think of it as a hotel key card rather than a house key: it is issued on
> arrival, works for under an hour, and is reissued rather than kept. The role is bound to that
> identity by object id, so the binding — not the name — is what grants access. Read
> **[ADR 0007](docs/decisions/0007-authenticate-to-postgres-with-managed-identity.md)** before
> touching the DB connection, and **[ADR 0001](docs/decisions/0001-postgres-password-authentication.md)**
> for the failure that made the first attempt at this fail.

### Current deployment (env: `budgetoid-prod`, region: `northeurope`)

| Resource | Value |
|---|---|
| Resource group | `rg-budgetoid-prod` — every command below targets it |
| Pipeline identity | `msi-budgetoid`, a user-assigned managed identity in its **own** resource group `rg-budgetoid-msi`. It is deliberately outside the group above: it is the identity that runs `azd provision`, so tearing the application infrastructure down must not take it with it. Moving it in would cost a recreated identity — user-assigned identities cannot be moved across groups, and its client id, the `AZURE_CLIENT_ID` variable, and all three federated credentials would have to be reissued. |
| Container App | `api`, in a Container Apps environment the AppHost owns and places on the virtual network below |
| Database networking | The API reaches PostgreSQL over a **private endpoint**; the server carries **no standing firewall rule**. Public access stays enabled purely so this pipeline can open a two-minute window for one address. A private DNS zone makes the server's ordinary public hostname resolve to its private address inside the network, so no connection string mentions any of this. |
| API database identity | `budgetoid_app`, the least-privilege role, bound by object id to the API's user-assigned managed identity; the password-free connection string is injected into the Container App as `ConnectionStrings__budgetoid` |
| API URL | `https://api.budgetoid.app`. Underneath it a Container Apps environment mints a fresh generated hostname every time it is recreated, so that one is a lookup and never a constant: `az containerapp show -n api -g rg-budgetoid-prod --query properties.configuration.ingress.fqdn -o tsv`. The custom domain is the layer of indirection that keeps a regenerated hostname from being a four-place edit ([ADR 0010](docs/decisions/0010-serve-the-app-from-a-custom-domain.md)). |
| Frontend URL | `https://budgetoid.app`, over a Static Web App whose generated `*.azurestaticapps.net` hostname is likewise private: `az staticwebapp show -n <name> -g rg-budgetoid-prod --query defaultHostname -o tsv` |

---

## Prerequisites (one-time)

- An Azure subscription + the **Azure Developer CLI** (`azd`) and **Azure CLI** (`az`).
- Access to the **Google Cloud** OAuth client used by the app.
- `dotnet-ef` (`dotnet tool install --global dotnet-ef --version 10.0.0`) for migrations.
- `psql` (the PostgreSQL client — `brew install libpq`, then put its `bin` on your `PATH`) for the
  role-provisioning step.

Interactive logins are easiest run from this session with the `!` prefix, e.g. `! azd auth login`.

---

## Step 1 — Create the Static Web App (frontend host)

Create it first so its URL is available when you provision the API (Step 2 prompts for it).

```sh
az staticwebapp create -n budgetoid-web -g <resource-group> -l westeurope --sku Free
```

- Note the default hostname (e.g. `https://<name>.azurestaticapps.net`) — this is the
  **frontend origin** used in Steps 2, 4 and 5.
- Copy the **deployment token** (`az staticwebapp secrets list -n budgetoid-web --query "properties.apiKey" -o tsv`) for Step 5.

## Step 2 — Provision infra + deploy the API (azd)

From the repo root:

```sh
azd auth login
azd init            # detects azure.yaml; name the environment e.g. "budgetoid-prod"
azd up              # provisions ACA + the Postgres Flexible Server, builds/pushes the image, deploys
                    # (azd regenerates the Bicep from the AppHost each run; ./infra is gitignored)
```

`azd up` prompts for subscription + region, then for the app parameters (wired in the AppHost, so
they land in the committed Bicep — no manual container-app edits):

| Prompt | Value |
|---|---|
| `google-client-id` | your Google OAuth client id |
| `frontend-origin` | `https://budgetoid.app`. It is injected twice — as the CORS allowed origin and as the passkey ceremony's allowed origin — because those are the same origin by definition. Not the generated Static Web App URL from Step 1: that hostname is private, and an origin the ceremony accepts is an origin passkeys get registered against. |
| `passkey-relying-party-id` | `budgetoid.app`, the registrable domain of that origin. **Frozen, and deliberately not a choice made here.** Every passkey an authenticator stores hashes this value into the credential, so changing it later does not re-point existing passkeys — it invalidates every one of them, and no migration repairs them. The generated `*.azurestaticapps.net` hostname is **never** an acceptable value, not even temporarily: an account created under it is an account whose passkeys die at cutover. Step 6 therefore binds the domain as part of bringing the environment up rather than after it. |
| `pipeline-principal-id` | the **object id** of the service principal that will deploy. `azd pipeline config` in Step 5 creates it; on a first bootstrap use your own principal's object id and re-run `azd up` after Step 5. It is registered as a Microsoft Entra administrator of the Postgres server, which is the only identity that can migrate the schema. |
| `pipeline-principal-name` | that principal's display name. Postgres needs a role name to log in as even though the token is what proves which principal it is. |

The database needs **no** password prompt and **no** connection-string prompt: the server is
Microsoft Entra only, and nothing in this deployment holds a database password
([ADR 0007](docs/decisions/0007-authenticate-to-postgres-with-managed-identity.md)). The API is
handed `Host=…;Username=budgetoid_app;Database=budgetoid` and fetches an access token from its own
managed identity to authenticate — the **absence** of a password in that string is what turns the
token provider on, so do not "complete" it. Note the API's public URL from the output. To change a
parameter later: `azd env set <name> <value>` then `azd up` — except `passkey-relying-party-id`,
which is permanent once passkeys exist (see the table above).

The API **refuses to boot** without the Google client id, the CORS origin and both passkey settings;
there is no `Api/appsettings.json` supplying defaults. That is deliberate: a container that starts
healthy and only fails when somebody attempts a sign-in reports its defect to a user instead of to
this pipeline. A missing parameter shows up as a crash-looping revision on the very deploy that
introduced it.

> **Note.** Earlier deploys needed a post-deploy step to repair a bare connection-string secret azd
> wrote. That is **root-fixed** — `AppHost/Program.cs` injects the connection string directly, so
> every `azd deploy` produces a complete, self-contained value. Do not replace it with a
> `WithReference`: for this resource type a reference would also register the API's managed identity
> as a full server administrator, and an administrator is not subject to the row-level security
> policies the whole tenancy design rests on. See gotcha #1 in
> [ADR 0001](docs/decisions/0001-postgres-password-authentication.md) and
> [ADR 0007](docs/decisions/0007-authenticate-to-postgres-with-managed-identity.md).

## Step 3 — Migrate the schema and provision the database role

**Every deploy does this automatically.** The pipeline runs it between `azd provision` and
`azd deploy`, so new application code never starts against an old schema. One command does the whole
job — `BudgetoidApp/Tools/DbProvision`, which migrates, provisions the `budgetoid_app` role with its
grants and row-level security policies, and then verifies that the policies actually cover every
budget-owned table.

The ordering used to live in this runbook and now lives in code, because getting it wrong is silent.
The grant matrix is fail-closed: a missing privilege announces itself as `42501` at the first
statement that needs it. Row-level security is fail-**open** — a migrated table with no enforced
policy is readable and writable by the application role across every tenant, and nothing reports it.
A deploy that migrated but skipped provisioning was therefore a tenancy breach you would not hear
about, which is why the tool verifies rather than assumes
([ADR 0006](docs/decisions/0006-automate-migrations-and-provisioning-in-the-pipeline.md)).

Migrations never run at API startup and never on the application role: `budgetoid_app` is denied
`CREATE` on the schema and cannot apply a migration even as a no-op
([ADR 0004](docs/decisions/0004-connect-as-a-least-privilege-role.md)). The tool authenticates as a
**Microsoft Entra administrator of the server** — it mints an access token for its own identity and
hands it to Postgres in the password field. There is no stored credential to read
([ADR 0007](docs/decisions/0007-authenticate-to-postgres-with-managed-identity.md)).

The tool also does one thing the pipeline cannot: it binds `budgetoid_app` to the API's managed
identity, by attaching a security label carrying that identity's object id. Azure matches a token to
a role by object id rather than by name, so this is what lets the API log in at all. Skipping it is
not silent — the API fails every request with `28P01` — which is why it sits outside the
migrate-then-provision-then-verify sequence rather than inside it.

### First bootstrap, and break-glass

On a brand-new environment the pipeline is not wired yet (that is Step 5), so run this once by hand
after Step 2 — otherwise the API is deployed against a database with no schema. The same recipe is
the break-glass path when the pipeline is unavailable. It requires your own principal to be an Entra
administrator of the server (Step 2's `pipeline-principal-id`).

```sh
SERVER=<postgres-server-name>
RG=rg-budgetoid-prod

# 1) let your current machine reach the DB (Azure Postgres blocks all IPs by default)
MYIP=$(curl -s https://api.ipify.org)
az postgres flexible-server firewall-rule create \
  --resource-group "$RG" --server-name "$SERVER" \
  --name AllowMigrationClient --start-ip-address "$MYIP" --end-ip-address "$MYIP"

# 2) migrate + provision + verify + bind the role to the API's identity. Both inputs are
#    environment variables, never arguments — argv is visible to other processes. The token is
#    valid for under an hour, so acquire it right before the run.
HOST=$(az postgres flexible-server show -g "$RG" -n "$SERVER" \
  --query fullyQualifiedDomainName -o tsv)
APP_IDENTITY_OID=$(az identity show \
  --ids "$(azd env get-value CAE_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID)" \
  --query principalId -o tsv)
TOKEN=$(az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv)
DBPROVISION_ADMIN_CONNECTION_STRING="Host=${HOST};Username=$(az ad signed-in-user show --query userPrincipalName -o tsv);Password=${TOKEN};Database=budgetoid;Ssl Mode=Require" \
DBPROVISION_APP_IDENTITY_OBJECT_ID="$APP_IDENTITY_OID" \
  dotnet run --project BudgetoidApp/Tools/DbProvision -c Release

# 3) SECURITY: remove your IP again
az postgres flexible-server firewall-rule delete \
  --resource-group "$RG" --server-name "$SERVER" --name AllowMigrationClient --yes
```

The firewall commands are spelled for a current Azure CLI, where `--name` is the *rule* and the
server is `--server-name`. A CLI old enough to reject `--server-name` wants `-n "$SERVER"
--rule-name AllowMigrationClient` instead — the same call with the two names swapped, which fails
loudly rather than quietly.

Exit codes: **0** provisioned, verified, and the role bound to the identity; **1** provisioning
failed; **2** a required environment variable is missing, empty, or malformed. On success the tool
prints what it did — how many migrations were pending, which tables it verified, and which identity
the role was bound to. A first run against a database migrated by hand should report no pending
migrations; that line is the evidence the histories agree.

If the `SECURITY LABEL` statement is rejected, the label's non-admin form is the suspect — the vendor
documentation only shows the admin-bearing variant. Bind the role by function call instead, as the
Entra admin, against the `postgres` database (the label provider lives only there):

```sh
PGPASSWORD="$TOKEN" psql "host=$HOST user=$(az ad signed-in-user show --query userPrincipalName -o tsv) dbname=postgres sslmode=require" \
  -c "select pgaadauth_create_principal_with_oid('budgetoid_app', '$APP_IDENTITY_OID', 'service', false, false)"
```

The role is never dropped either way, so its grants and policies survive the attempt.

### Resetting the migration history after a rebaseline

A regenerated baseline carries a new migration id. Production's `__EFMigrationsHistory` still names
the old one, so the pipeline finds nothing applied and runs the new baseline against a schema that
already exists — the deploy dies on the first `CREATE TABLE`. The fix is to hand the database back
its empty state so the new baseline is true: **drop the schema, then migrate from scratch.**

This is destructive and unconditional. It is available only because the production database holds no
data, and it belongs in the same deploy that ships the regenerated baseline — never as a follow-up.
Whether the rebaseline is permitted at all is recorded in the `migrations-guard` CI job
(`REBASELINE_WINDOW`); [migrations](docs/engineering/migrations.md) explains when that window closes.

Set up `$HOST` and `$TOKEN` exactly as in the break-glass recipe above, including the firewall rule,
then, as an Entra administrator of the server:

```sh
PGPASSWORD="$TOKEN" psql "host=$HOST user=$(az ad signed-in-user show --query userPrincipalName -o tsv) dbname=budgetoid sslmode=require" \
  -c 'drop schema public cascade' -c 'create schema public'
```

That takes `__EFMigrationsHistory`, every table, and the `case_insensitive` ICU collation with it.
All three come back from the baseline — the collation is a model-level annotation the migration
emits, not a hand-run statement. Then run the same `DbProvision` command the break-glass recipe
uses: it migrates the fresh schema, re-runs `app-role-grants.sql` (idempotent by design, and the
source of the `USAGE` grant on the recreated schema), verifies row-level security coverage, and
rebinds `budgetoid_app` to the API's managed identity. The role itself is never dropped, so only its
grants need restoring, and provisioning restores them.

Then remove the firewall rule, as always.

### Verifying by hand

The tool's own verification covers row-level security. To inspect the grant matrix as well:

```sh
export PGHOST="$HOST"
export PGUSER=$(az ad signed-in-user show --query userPrincipalName -o tsv)
export PGPASSWORD=$(az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv)
export PGDATABASE=budgetoid PGSSLMODE=require
psql -c "\du budgetoid_app"
psql -c "\dp payees"
psql -c "select tablename, policyname from pg_policies where schemaname = 'public' order by tablename"

# and that the role is bound to the API's identity (this one is on the postgres database)
PGDATABASE=postgres psql -c "select rolename, principaltype, objectid from pgaadauth_list_principals(false)"
```

`PGPASSWORD` here is an access token, which is why it must be exported rather than typed at a prompt:
it is far longer than `psql` accepts interactively.

`\dp payees` should show `budgetoid_app=ar/…` under **Access privileges** (SELECT + INSERT) and
`name: budgetoid_app=w/…` under **Column privileges** (UPDATE on that column alone). `budget_id`
must not appear anywhere in that row — its absence from the column list is what makes it immutable,
since PostgreSQL column privileges are additive and a `REVOKE` could not express it. The
`pg_policies` query should return five rows, one `budget_isolation` policy each on `accounts`,
`categories`, `category_groups`, `payees` and `transactions`. Fewer means the role can read every
tenant's rows in whichever table is missing one — and it means the tool's verification would have
failed, so seeing this by hand should be impossible after a green deploy.

### Troubleshooting

- `28P01` from the deployed API means the role is not bound to its identity, or is bound to the wrong
  object id. Check `pgaadauth_list_principals(false)` against
  `az containerapp show -n api -g rg-budgetoid-prod --query "identity.userAssignedIdentities.*.principalId"`.
  Azure matches tokens to roles by object id, so a correct role *name* proves nothing.
- `ResourceNotFound` for `Microsoft.App/containerApps/api` during the pipeline's provisioning step
  means something is asking the container app about itself before it exists. The app is created by
  `azd deploy`, which runs *after* the database work on purpose, so it is absent on any deploy that
  starts without one — a first deploy, or one after the environment was rebuilt. The identity to ask
  instead is the one the Container Apps environment owns
  (`azd env get-value CAE_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID` — azd prefixes a module's
  outputs with the resource name, and the unprefixed spelling survives only in local environments
  provisioned before the AppHost owned the Container Apps environment); it exists from provisioning
  onward and outlives every app in the environment.
- `28000` mentioning `no pg_hba.conf entry` for a user like `app` means the connection string lost its
  `Username=budgetoid_app` and Npgsql fell back to the container's OS user. That is ADR 0001's
  original failure mode; the username is not optional under Entra auth.
- A `FATAL: … oid mismatch` names exactly this: the token's principal and the label's `oid` disagree.
  Re-run the provisioning tool, which is idempotent and re-applies the label.
- The admin identity must own the tables (`ALTER TABLE … ENABLE ROW LEVEL SECURITY` and
  `CREATE POLICY` are owner operations) and must have created the role (`ALTER ROLE … SET` needs
  `CREATEROLE` over it). Migrations run on that same identity, so it owns them; a `42501` on an
  `ALTER TABLE` or `ALTER ROLE` points at ownership.
- A feature failing in production with `42501` means the role is missing a privilege: add the
  narrowest grant to `app-role-grants.sql` and let the next deploy apply it, never `GRANT ALL`. Two
  other codes point at the database rather than the application: `42501` naming a row-level security
  policy means a write tried to land in a budget other than the request's, and `22P02` on an
  empty-string `uuid` cast means a connection reached a budget-owned table without an ambient budget
  on the session.
- `42501` on the `SECURITY LABEL` statement means the connecting principal is not an Entra
  administrator of the server. Membership in `azure_pg_admin` is not enough; only an Entra
  administrator can create or label Entra principals. Register it with
  `az postgres flexible-server microsoft-entra-admin create -g rg-budgetoid-prod -s <server> -u <displayName> -i <objectId> -t ServicePrincipal`
  (the subcommand was `ad-admin` before Azure CLI 2.86.0, and the old name is no longer recognised).
  Verify with `microsoft-entra-admin list` — an empty list on an `activeDirectoryAuth: Enabled`,
  `passwordAuth: Disabled` server means **nobody** can administer it, which is what a provision
  interrupted before the administrator resource leaves behind. Re-running `azd provision` converges it.
- A token expires in under an hour. A long manual session that starts failing to open *new*
  connections has a stale token, not a broken configuration — re-export `PGPASSWORD`.

## Step 4 — Point the frontend at prod + set OAuth redirect

1. Edit `ClientApp/angular-budgetoid/public/assets/app-config.json`, replacing the placeholders:
   - `apiBaseUrl` → the API URL from Step 2.
   - `auth.google.redirectUri` → the Static Web App URL from Step 1.
   Commit the change.
2. In the **Google Cloud console** → the OAuth 2.0 client → add the Static Web App URL from Step 1
   to **Authorized JavaScript origins** and **Authorized redirect URIs**.

## Step 5 — GitOps: wire the pipeline (one-time)

`.github/workflows/deploy.yml` deploys on every push to `main` (and via manual `workflow_dispatch`).
It needs these GitHub secrets/vars:

| Kind | Name | Value |
|---|---|---|
| secret | `AZURE_STATIC_WEB_APPS_API_TOKEN` | SWA deployment token (Step 1) |
| var | `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | from `azd pipeline config` |
| var | `AZURE_ENV_NAME` / `AZURE_LOCATION` | your azd env name + region |
| var | `AZURE_FRONTEND_ORIGIN` / `AZURE_GOOGLE_CLIENT_ID` / `AZURE_PASSKEY_RELYING_PARTY_ID` | the Step 2 app parameters. The CI config store is empty, so azd reads them from here; the API refuses to boot without any of them |
| var | `AZURE_PIPELINE_PRINCIPAL_ID` / `AZURE_PIPELINE_PRINCIPAL_NAME` | the deploy principal's object id and display name — they register it as an Entra administrator of the Postgres server |

There is no database secret in that table, and that is the point: the pipeline authenticates to
Postgres with a token it mints for its own federated identity
([ADR 0007](docs/decisions/0007-authenticate-to-postgres-with-managed-identity.md)). The two
`AZURE_PIPELINE_PRINCIPAL_*` values are identifiers rather than credentials, so they are variables,
not secrets — neither authenticates anything without the OIDC login.

Run these once, in order:

```sh
# 1) mint the federated (OIDC) app registration + set the AZURE_* GitHub vars automatically
azd pipeline config --provider github

# 2) the SWA token is out-of-band (not an azd concept) — set it manually
gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN \
  --body "$(az staticwebapp secrets list -n budgetoid-web --query 'properties.apiKey' -o tsv)"

# 3) the deploy principal's identifiers. azd provision registers it as an Entra administrator of the
#    Postgres server from these, and the migration step logs in as it.
SP_OID=$(az ad sp show --id <AZURE_CLIENT_ID> --query id -o tsv)
gh variable set AZURE_PIPELINE_PRINCIPAL_ID --body "$SP_OID"
gh variable set AZURE_PIPELINE_PRINCIPAL_NAME \
  --body "$(az ad sp show --id <AZURE_CLIENT_ID> --query displayName -o tsv)"
```

The pipeline identity needs management-plane access (`Contributor`) **and** must be a Microsoft Entra
administrator of the Postgres server, which `azd provision` now arranges from the variables above.
That administrator role is the one privilege automation costs: the alternative is an operator holding
the same standing and running the same commands, which is what Step 3 replaced. It is still an
improvement on what it replaced — an Entra administrator holds no password, and revoking it is a
single `microsoft-entra-admin delete` rather than a credential rotation.

After that, pushing to `main` provisions, migrates, provisions the database role, binds it to the
API's managed identity, and deploys — in that order. You can still trigger a manual run from the
**Actions** tab.

## Step 6 — Custom domain (`budgetoid.app`)

**Not a cutover — part of the first provision.** There is no production environment today:
`rg-budgetoid-prod` does not exist, and the generated hostnames earlier revisions of this document
quoted answer nothing. The domain is registered at Cloudflare Registrar, its nameservers are live,
and the target layout is decided ([ADR 0010](docs/decisions/0010-serve-the-app-from-a-custom-domain.md)),
but the zone holds no records yet.

That ordering is the point. `passkey-relying-party-id` is frozen at `budgetoid.app` before Step 2 is
ever answered, so this step runs while the environment is still empty of accounts. Standing an
environment up on its generated hostnames and moving the domain afterwards would register passkeys
against a name that is about to stop existing, and no migration repairs those.

| Name | Serves | Record |
|---|---|---|
| `budgetoid.app` | frontend (SWA) | `CNAME` → SWA hostname, flattened at the apex |
| `api.budgetoid.app` | API (Container Apps) | `CNAME` → container app FQDN, plus an `asuid` `TXT` |
| `www.budgetoid.app` | — | redirect rule to the apex |

DNS-only throughout — the Cloudflare proxy stays off (grey cloud). Both Azure services validate a
custom domain by resolving it and inspecting what answers, and a proxied record answers with
Cloudflare's address, so validation fails while the orange cloud is on. If the proxy is ever wanted,
it goes on **after** both certificates are issued, never before.

**`.app` is HSTS-preloaded at the TLD level**, so there is no plaintext fallback and no certificate
warning to click past. A hostname whose certificate has not been issued is unreachable, not degraded.
That is why the DNS record moves last in each block below.

```sh
# --- API: api.budgetoid.app -----------------------------------------------------------------
# 1) the validation token Azure expects at asuid.<subdomain>
az containerapp show -n <api-app> -g rg-budgetoid-prod \
  --query "properties.customDomainVerificationId" -o tsv

# In Cloudflare, DNS-only:
#   TXT    asuid.api    <the value above>
#   CNAME  api          <api-app>.<env-suffix>.northeurope.azurecontainerapps.io

# 2) bind it and let Azure issue the managed certificate (a few minutes)
az containerapp hostname add -n <api-app> -g rg-budgetoid-prod --hostname api.budgetoid.app
az containerapp hostname bind -n <api-app> -g rg-budgetoid-prod \
  --hostname api.budgetoid.app --environment cae --validation-method CNAME

# --- Frontend: budgetoid.app ----------------------------------------------------------------
# The apex is validated by TXT (SWA cannot use CNAME validation at a zone apex). The command
# prints the record to create; add it in Cloudflare, then re-run to complete validation.
az staticwebapp hostname set -n budgetoid-web -g rg-budgetoid-prod \
  --hostname budgetoid.app --validation-method dns-txt-token

# Then, DNS-only:
#   CNAME  @    <name>.azurestaticapps.net      (Cloudflare flattens this at the apex)
```

Once both certificates are issued, four places name a hostname and each has to agree. Missing any one
of them leaves a deployment that looks healthy and is not:

1. `ClientApp/angular-budgetoid/public/assets/app-config.json` — **already committed** with
   `apiBaseUrl` → `https://api.budgetoid.app` and `auth.google.redirectUri` → `https://budgetoid.app`.
   Nothing to do here unless somebody has pointed it back at a generated hostname.
2. **The azd environment**, not just the repo: `azd env set AZURE_FRONTEND_ORIGIN
   https://budgetoid.app` and `azd env set AZURE_PASSKEY_RELYING_PARTY_ID budgetoid.app`. The first
   is what the next `azd provision` bakes into the container app as `Cors__AllowedOrigins__0`
   **and** as `Authentication__Passkey__AllowedOrigins__0`; forget it and the browser reports a
   network failure that is really a CORS rejection. The second is set once and never again — a
   passkey registered under one relying party id cannot be re-pointed at another, so the value is
   frozen before the environment exists rather than reconsidered here.
3. **Google Cloud console** → the OAuth 2.0 client → add `https://budgetoid.app` to **Authorized
   JavaScript origins** and **Authorized redirect URIs**. Nothing in this repository can verify this
   step; it is the one that breaks login while everything else reports success.
4. `www.budgetoid.app` → a Cloudflare redirect rule to the apex. Without a record it is `NXDOMAIN`.

**There is no dual-origin rollback, and that is deliberate.** Keeping the generated hostnames
alongside the new domain "until it is confirmed working" is the obvious safety net and it is a trap:
`frontend-origin` is injected into `Cors__AllowedOrigins__0` **and**
`Authentication__Passkey__AllowedOrigins__0` from one parameter, so an origin kept for rollback is an
origin the passkey ceremony accepts — and a passkey registered there is bound to a relying party id
that is about to stop existing. The rollback is to fix the DNS, not to widen the origin list. A
second Google redirect URI is harmless and may stay; a second allowed origin may not.

Auto-renew on the domain must stay **on**. An expired `.app` is a total outage with no partial
failure to notice first.

---

## Scale-up ladder (turn these as load / users grow)

1. Cold starts noticeable → set the API `MinReplicas = 1`.
2. Need an uptime SLA on the frontend → SWA **Standard**.
3. DB CPU/IO saturating → move Postgres to **General Purpose**; then add **zone-redundant HA**.
4. Add a **staging** environment + **App Insights** + alerts.

## Verify (end-to-end)

1. `aspire run` locally still works (dev CORS to `localhost:4200`, local Postgres container).
2. DB: the deploy run's provisioning step exits 0 — it reports the migrations it applied, then
   confirms row-level security covers every budget-owned table. `\dp payees` shows `budgetoid_app`
   with the column grants, and `az postgres flexible-server firewall-rule list` comes back empty.
   **Empty is the whole point and it is not self-maintaining.** ARM deployments are incremental, so
   a rule that already exists on the server survives being deleted from the template — after the
   deployment that introduced the private endpoint, `AllowAllAzureIps` had to be removed by hand
   (`az postgres flexible-server firewall-rule delete --resource-group rg-budgetoid-prod
   --server-name <server> --name AllowAllAzureIps --yes`). Anything listed here between deploys is
   either that rule returning or a window a killed runner stranded.
3. API: `curl https://<api-url>/health` → `200`. Note that this proves nothing about the database —
   the health check does not touch it. A request that reads or writes data is the only thing that
   exercises the private endpoint, and with no firewall rule standing, a successful one is proof
   the traffic went private: the public path would have refused it.
4. Frontend: open the SWA URL, sign in with Google (redirect accepted), create/list/edit/delete a
   transaction — no CORS errors in the browser console.
5. Security headers, on both origins and on a **deep link** as well as the root:

   ```sh
   for url in https://budgetoid.app/ https://budgetoid.app/app/settings \
              https://api.budgetoid.app/health; do
     echo "== $url"
     curl -sI "$url" | grep -iE \
       '^(content-security-policy|strict-transport-security|referrer-policy|x-content-type-options):'
   done
   ```

   The two frontend URLs must each answer with all three headers, and the API with four
   (`X-Content-Type-Options: nosniff` is the API's alone). **The deep link is the one that matters:**
   Azure applies no route rule to a request `navigationFallback` rewrote, so headers moved out of
   `globalHeaders` onto a `/*` route are present on the root and absent on every URL a person lands
   on. Nothing in this repository can check any of this — `src/security-headers.spec.ts` and
   `SecurityHeaderTests` prove the configuration and the middleware ship with these values, not that
   Azure emits them ([security headers](docs/engineering/security-headers.md)).
