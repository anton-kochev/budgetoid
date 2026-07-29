# Deploying Budgetoid to Azure

Budgetoid is a **production service**. This baseline starts at the cheapest production-viable
tier (real backups + security, minimal spend) with a clear path to scale up on load.

| Component | Host (start) | Scale-up lever |
|---|---|---|
| API (.NET 10) | Azure Container Apps, consumption, scale-to-zero (`MinReplicas = 0`) | → `MinReplicas = 1` when cold starts bite |
| Frontend (Angular) | Azure Static Web Apps, **Free** (custom domain + TLS included) | → Standard for the SLA |
| PostgreSQL | Azure Postgres **Flexible Burstable B1ms**, 32 GB, 7-day backups + PITR | → General Purpose → zone-redundant HA |
| DB auth | **Password**: an Aspire-generated admin login in **Azure Key Vault** for migrations and provisioning, and the least-privilege `budgetoid_app` role the API serves requests as (see [ADR 0004](docs/decisions/0004-connect-as-a-least-privilege-role.md)) | → back to passwordless (see [ADR 0001](docs/decisions/0001-postgres-password-authentication.md)) |

**Infra is code.** The Aspire `AppHost` is the source of truth; `azd` provisions **the API, the
PostgreSQL server, and its Key Vault** from it, regenerating the Bicep at deploy time (the
synthesized `./infra` is gitignored, not committed, to avoid drift). The **Static Web App is the
only out-of-band resource** — created once with one command.

> **Why password auth and not passwordless?** Aspire's passwordless (managed-identity) client
> wiring produced an incomplete connection string, and every DB request 500'd. The full rationale,
> the two rollout gotchas, and the hardening path back to passwordless are in
> **[ADR 0001](docs/decisions/0001-postgres-password-authentication.md)**. Read it before touching
> the DB connection.

### Current deployment (env: `budgetoid-prod`, region: `northeurope`)

| Resource | Value |
|---|---|
| Resource group | `rg-budgetoid-prod` |
| Container App | `api` |
| Key Vault | `postgreskv-bq7exijxgtbdu` (RBAC mode) |
| Key Vault secret | `connectionstrings--budgetoid` — the **admin** connection string, used by Steps 3 and 4 |
| API database identity | `budgetoid_app`, the least-privilege role; its connection string is injected into the Container App as `ConnectionStrings__budgetoid` |
| API URL | `https://api.purpletree-58c68a6f.northeurope.azurecontainerapps.io` |
| Frontend URL | `https://ashy-water-0fc187003.7.azurestaticapps.net` |

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
azd up              # provisions ACA + the Postgres Flexible Server + Key Vault, builds/pushes the image, deploys
                    # (azd regenerates the Bicep from the AppHost each run; ./infra is gitignored)
```

`azd up` prompts for subscription + region, then for three app parameters (wired in the AppHost, so
they land in the committed Bicep — no manual container-app edits):

| Prompt | Value |
|---|---|
| `google-client-id` | your Google OAuth client id |
| `frontend-origin` | the Static Web App URL from Step 1 |
| `postgres-app-password` | a password you choose for the `budgetoid_app` database role. **Step 3 assigns this exact value to the role**, and the API's connection string is built from it — so the same value goes into the `AZURE_POSTGRES_APP_PASSWORD` secret in Step 5. Restrict it to ASCII letters, digits and `-_.~!@#%^*+=` — it is spliced into SQL as a literal (see [ADR 0004](docs/decisions/0004-connect-as-a-least-privilege-role.md)). |

The database needs **no** connection-string prompt. Aspire's `.WithPasswordAuthentication()`
generates a strong **admin** password and stores it in the provisioned **Key Vault**, where Steps 3
and 4 read it from; the AppHost separately builds the API's connection string from the server host
and `postgres-app-password`, so the container is handed the least-privilege identity and never the
administrator's. Note the API's public URL from the output. To change a parameter later:
`azd env set <name> <value>` then `azd up`.

> **Note.** Earlier deploys needed a post-deploy step to repair a bare connection-string secret azd
> wrote. That is **root-fixed** — `AppHost/Program.cs` now injects the full connection string
> directly, so every `azd deploy` produces a complete, self-contained secret. See gotcha #1 in
> [ADR 0001](docs/decisions/0001-postgres-password-authentication.md).

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
([ADR 0004](docs/decisions/0004-connect-as-a-least-privilege-role.md)). The tool authenticates with
the **admin password Aspire stored in Key Vault**.

### First bootstrap, and break-glass

On a brand-new environment the pipeline is not wired yet (that is Step 5), so run this once by hand
after Step 2 — otherwise the API is deployed against a database with no schema. The same recipe is
the break-glass path when the pipeline is unavailable.

```sh
# 1) let your current machine reach the DB (Azure Postgres blocks all IPs by default)
MYIP=$(curl -s https://api.ipify.org)
az postgres flexible-server firewall-rule create \
  -g rg-budgetoid-prod -n <postgres-server-name> \
  --rule-name AllowMigrationClient --start-ip-address "$MYIP" --end-ip-address "$MYIP"

# 2) migrate + provision + verify. Ssl Mode=Require is appended because the Key Vault string
#    carries host/user/password/database and no SslMode, and Azure refuses unencrypted connections.
#    Both inputs are environment variables, never arguments — argv is visible to other processes.
CONN=$(az keyvault secret show --vault-name postgreskv-bq7exijxgtbdu \
  --name connectionstrings--budgetoid --query value -o tsv)
DBPROVISION_ADMIN_CONNECTION_STRING="${CONN};Ssl Mode=Require" \
DBPROVISION_APP_ROLE_PASSWORD='<the postgres-app-password value from Step 2>' \
  dotnet run --project BudgetoidApp/Tools/DbProvision -c Release

# 3) SECURITY: remove your IP again
az postgres flexible-server firewall-rule delete \
  -g rg-budgetoid-prod -n <postgres-server-name> --rule-name AllowMigrationClient --yes
```

Exit codes: **0** provisioned and verified, **1** provisioning failed, **2** a required environment
variable is missing or empty. On success the tool prints what it did — how many migrations were
pending, and which tables it verified. A first run against a database migrated by hand should report
no pending migrations; that line is the evidence the histories agree.

### Verifying by hand

The tool's own verification covers row-level security. To inspect the grant matrix as well:

```sh
export PGHOST=$(echo "$CONN" | sed -n 's/.*Host=\([^;]*\).*/\1/p')
export PGUSER=$(echo "$CONN" | sed -n 's/.*Username=\([^;]*\).*/\1/p')
export PGPASSWORD=$(echo "$CONN" | sed -n 's/.*Password=\([^;]*\).*/\1/p')
export PGDATABASE=budgetoid PGSSLMODE=require
psql -c "\du budgetoid_app"
psql -c "\dp payees"
psql -c "select tablename, policyname from pg_policies where schemaname = 'public' order by tablename"
```

`\dp payees` should show `budgetoid_app=ar/…` under **Access privileges** (SELECT + INSERT) and
`name: budgetoid_app=w/…` under **Column privileges** (UPDATE on that column alone). `budget_id`
must not appear anywhere in that row — its absence from the column list is what makes it immutable,
since PostgreSQL column privileges are additive and a `REVOKE` could not express it. The
`pg_policies` query should return five rows, one `budget_isolation` policy each on `accounts`,
`categories`, `category_groups`, `payees` and `transactions`. Fewer means the role can read every
tenant's rows in whichever table is missing one — and it means the tool's verification would have
failed, so seeing this by hand should be impossible after a green deploy.

### Troubleshooting

- Keep the role password inside the alphabet the grants script's substitution assumes: ASCII
  letters, digits and `-_.~!@#%^*+=`. The tool refuses anything else **before** touching the
  database, so a bad password cannot leave a half-migrated schema behind.
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
- Reading the Key Vault secret by hand needs the **Key Vault Secrets User** role on `postgreskv-…`
  (the vault is RBAC-mode). Grant it to yourself once:
  `az role assignment create --assignee "$(az ad signed-in-user show --query id -o tsv)" --role "Key Vault Secrets User" --scope "$(az keyvault show -n postgreskv-bq7exijxgtbdu --query id -o tsv)"`
- The `sed` extraction above assumes the Key Vault string carries no quoted values — if the admin
  password ever contains a `;`, set `PGHOST`/`PGUSER`/`PGPASSWORD` by hand.

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
| secret | `AZURE_POSTGRES_APP_PASSWORD` | the `postgres-app-password` value from Step 2 |
| var | `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | from `azd pipeline config` |
| var | `AZURE_ENV_NAME` / `AZURE_LOCATION` | your azd env name + region |

Run these once, in order:

```sh
# 1) mint the federated (OIDC) app registration + set the AZURE_* GitHub vars automatically
azd pipeline config --provider github

# 2) the SWA token is out-of-band (not an azd concept) — set it manually
gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN \
  --body "$(az staticwebapp secrets list -n budgetoid-web --query 'properties.apiKey' -o tsv)"

# 3) the application role password. One secret, two consumers that must agree: azd bakes it into the
#    container's connection string, and the provisioning step assigns it to the role. Use the value
#    the role already has — a different one would re-password the role a minute after the container
#    picked up the new string, and a cold start in that window fails with 28P01.
gh secret set AZURE_POSTGRES_APP_PASSWORD --body '<the postgres-app-password value from Step 2>'

# 4) the pipeline reads the admin connection string from Key Vault, so it needs the data plane
az role assignment create \
  --assignee-object-id "$(az ad sp show --id <AZURE_CLIENT_ID> --query id -o tsv)" \
  --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" \
  --scope "$(az keyvault show -n postgreskv-bq7exijxgtbdu --query id -o tsv)"
```

The pipeline identity needs management-plane access (`Contributor`) **and**, since it migrates the
database, **Key Vault Secrets User** on the vault holding the admin connection string. That is the
one privilege automation costs: the alternative is an operator holding the same credential and
running the same commands, which is what Step 3 replaced.

After that, pushing to `main` provisions, migrates, provisions the database role, and deploys —
in that order. You can still trigger a manual run from the **Actions** tab.

---

## Scale-up ladder (turn these as load / users grow)

1. Cold starts noticeable → set the API `MinReplicas = 1`.
2. Need an uptime SLA on the frontend → SWA **Standard**.
3. DB CPU/IO saturating → move Postgres to **General Purpose**; then add **zone-redundant HA**.
4. Add a **staging** environment + **App Insights** + alerts + a **custom domain**.
5. Revisit **passwordless** DB auth (ADR 0001 hardening path) — it would also remove the pipeline's
   Key Vault grant, since there would be no admin password to read.

## Verify (end-to-end)

1. `aspire run` locally still works (dev CORS to `localhost:4200`, local Postgres container).
2. DB: the deploy run's provisioning step exits 0 — it reports the migrations it applied, then
   confirms row-level security covers every budget-owned table. `\dp payees` shows `budgetoid_app`
   with the column grants, and `az postgres flexible-server firewall-rule list` comes back empty.
3. API: `curl https://<api-url>/health` → `200`.
4. Frontend: open the SWA URL, sign in with Google (redirect accepted), create/list/edit/delete a
   transaction — no CORS errors in the browser console.
