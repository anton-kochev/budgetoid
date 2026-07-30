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
| API URL | `https://api.icyisland-d82f1c17.northeurope.azurecontainerapps.io` — a Container Apps environment mints a new hostname every time it is recreated, so treat this as a lookup, not a constant: `az containerapp show -n api -g rg-budgetoid-prod --query properties.configuration.ingress.fqdn -o tsv` |
| Frontend URL | `https://blue-island-06a7efa03.7.azurestaticapps.net` |

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

`azd up` prompts for subscription + region, then for three app parameters (wired in the AppHost, so
they land in the committed Bicep — no manual container-app edits):

| Prompt | Value |
|---|---|
| Prompt | Value |
|---|---|
| `google-client-id` | your Google OAuth client id |
| `frontend-origin` | the Static Web App URL from Step 1 |
| `pipeline-principal-id` | the **object id** of the service principal that will deploy. `azd pipeline config` in Step 5 creates it; on a first bootstrap use your own principal's object id and re-run `azd up` after Step 5. It is registered as a Microsoft Entra administrator of the Postgres server, which is the only identity that can migrate the schema. |
| `pipeline-principal-name` | that principal's display name. Postgres needs a role name to log in as even though the token is what proves which principal it is. |

The database needs **no** password prompt and **no** connection-string prompt: the server is
Microsoft Entra only, and nothing in this deployment holds a database password
([ADR 0007](docs/decisions/0007-authenticate-to-postgres-with-managed-identity.md)). The API is
handed `Host=…;Username=budgetoid_app;Database=budgetoid` and fetches an access token from its own
managed identity to authenticate — the **absence** of a password in that string is what turns the
token provider on, so do not "complete" it. Note the API's public URL from the output. To change a
parameter later: `azd env set <name> <value>` then `azd up`.

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
APP_IDENTITY_OID=$(az containerapp show -n api -g "$RG" \
  --query "identity.userAssignedIdentities.*.principalId | [0]" -o tsv)
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

---

## Scale-up ladder (turn these as load / users grow)

1. Cold starts noticeable → set the API `MinReplicas = 1`.
2. Need an uptime SLA on the frontend → SWA **Standard**.
3. DB CPU/IO saturating → move Postgres to **General Purpose**; then add **zone-redundant HA**.
4. Add a **staging** environment + **App Insights** + alerts + a **custom domain**.

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
