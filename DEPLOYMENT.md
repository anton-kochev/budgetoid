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
  **frontend origin** used in Steps 2, 5 and 6.
- Copy the **deployment token** (`az staticwebapp secrets list -n budgetoid-web --query "properties.apiKey" -o tsv`) for Step 6.

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
| `postgres-app-password` | a password you choose for the `budgetoid_app` database role. **Step 4 assigns this exact value to the role**, and the API's connection string is built from it. Restrict it to ASCII letters, digits and `-_.~!@#%^*+=` — it is spliced into SQL as a literal (see [ADR 0004](docs/decisions/0004-connect-as-a-least-privilege-role.md)). |

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

## Step 3 — Apply database migrations

Migrations run from the EF bundle, never at API startup. Authenticate with the **admin password
Aspire stored in Key Vault**: the deployed API connects as the least-privilege `budgetoid_app` role,
which is denied `CREATE` on the schema and cannot apply a migration even as a no-op
([ADR 0004](docs/decisions/0004-connect-as-a-least-privilege-role.md)). Leave the firewall rule this
step creates in place — Step 4 runs on the same connection and removes the rule at the end.

```sh
# 1) let your current machine reach the DB (Azure Postgres blocks all IPs by default)
MYIP=$(curl -s https://api.ipify.org)
az postgres flexible-server firewall-rule create \
  -g rg-budgetoid-prod -n <postgres-server-name> \
  --rule-name AllowMigrationClient --start-ip-address "$MYIP" --end-ip-address "$MYIP"

# 2) build the bundle
dotnet ef migrations bundle --project BudgetoidApp/Infrastructure \
  --startup-project BudgetoidApp/Api --configuration Release -o ./efbundle

# 3) apply it with the Key Vault connection string (append Ssl Mode=Require — Azure requires TLS)
CONN=$(az keyvault secret show --vault-name postgreskv-bq7exijxgtbdu \
  --name connectionstrings--budgetoid --query value -o tsv)
./efbundle --connection "${CONN};Ssl Mode=Require"
```

> Reading the Key Vault secret needs the **Key Vault Secrets User** role on `postgreskv-…` (the
> vault is RBAC-mode). Grant it to yourself once:
> `az role assignment create --assignee "$(az ad signed-in-user show --query id -o tsv)" --role "Key Vault Secrets User" --scope "$(az keyvault show -n postgreskv-bq7exijxgtbdu --query id -o tsv)"`
>
> Automating migrations in CI (granting the pipeline identity DB access) is a hardening follow-up —
> see the note in `deploy.yml`. Manual application is fine for now.

## Step 4 — Provision the application database role

The API serves every request as `budgetoid_app`, a least-privilege role whose grants express the
domain's immutability rules at the database (see
[ADR 0004](docs/decisions/0004-connect-as-a-least-privilege-role.md)) and whose row-level security
policies keep it inside one budget's rows (see
[ADR 0005](docs/decisions/0005-isolate-budget-owned-rows-with-row-level-security.md)). The role, its
grants and its policies all come from
`BudgetoidApp/Infrastructure/Persistence/Provisioning/app-role-grants.sql`, applied on the same admin
connection Step 3 used, with the `__APP_PASSWORD__` token replaced by the role password as a
single-quoted SQL literal — the same substitution `DatabaseProvisioning.ApplyGrantsAsync` performs.
Skipping this step does not merely block writes: without the policies every budget-owned table is
readable across every tenant.

**Two things to get right, because either one yields a deployment that cannot reach its database:**

- **The password here must be the exact value of the `postgres-app-password` azd parameter** from
  Step 2. That parameter is what the container's connection string is built from; this step is what
  the role is actually created with, and nothing reconciles the two.
- **This step must run after Step 3.** The grants and the policies name individual tables, so the
  schema has to exist before they can be applied.

The admin identity needs to own the tables (`ALTER TABLE … ENABLE ROW LEVEL SECURITY` and
`CREATE POLICY` are owner operations) and to have created the role (`ALTER ROLE … SET` needs
`CREATEROLE` over it). Step 3 runs the migrations on this same identity, so it owns them; if this
step fails with `42501` on an `ALTER TABLE` or `ALTER ROLE`, that ownership is what to check.

```sh
# 1) the same admin connection string as Step 3, split into what psql wants (libpq's keyword names
#    differ from Npgsql's, so reuse the values rather than the string)
CONN=$(az keyvault secret show --vault-name postgreskv-bq7exijxgtbdu \
  --name connectionstrings--budgetoid --query value -o tsv)
export PGHOST=$(echo "$CONN" | sed -n 's/.*Host=\([^;]*\).*/\1/p')
export PGUSER=$(echo "$CONN" | sed -n 's/.*Username=\([^;]*\).*/\1/p')
export PGPASSWORD=$(echo "$CONN" | sed -n 's/.*Password=\([^;]*\).*/\1/p')
export PGDATABASE=budgetoid PGSSLMODE=require

# 2) apply the grants, substituting the token with a single-quoted literal
APP_PASSWORD='<the postgres-app-password value from Step 2>'
sed "s/__APP_PASSWORD__/'$APP_PASSWORD'/g" \
  BudgetoidApp/Infrastructure/Persistence/Provisioning/app-role-grants.sql \
  | psql -v ON_ERROR_STOP=1 -f -

# 3) verify: the role exists, payees is writable by name only, and every budget-owned table is
#    policied — the grants are fail-closed but a missing policy is silent
psql -c "\du budgetoid_app"
psql -c "\dp payees"
psql -c "select tablename, policyname from pg_policies where schemaname = 'public' order by tablename"

# 4) SECURITY: remove your IP again (the rule Step 3 created)
az postgres flexible-server firewall-rule delete \
  -g rg-budgetoid-prod -n <postgres-server-name> --rule-name AllowMigrationClient --yes
```

`\dp payees` should show `budgetoid_app=ar/…` under **Access privileges** (SELECT + INSERT) and
`name: budgetoid_app=w/…` under **Column privileges** (UPDATE on that column alone). `budget_id`
must not appear anywhere in that row — its absence from the column list is what makes it immutable,
since PostgreSQL column privileges are additive and a `REVOKE` could not express it. The
`pg_policies` query should return five rows, one `budget_isolation` policy each on `accounts`,
`categories`, `category_groups`, `payees` and `transactions`. Fewer means the role can read every
tenant's rows in whichever table is missing one.

Keep the role password inside the alphabet the script's substitution assumes: ASCII letters, digits
and `-_.~!@#%^*+=`. `DatabaseProvisioning` refuses anything else before splicing it into SQL; the
`sed` above performs no such check, and characters outside that set can break the SQL literal, the
`sed` replacement, or both. The `sed` extraction of the admin values assumes the Key Vault string
carries no quoted values — if the admin password ever contains a `;`, pass `PGHOST`/`PGUSER`/
`PGPASSWORD` by hand instead.

Re-run this step on **every** deploy. The script is idempotent — each table is revoked and
re-granted and each policy dropped and recreated, so a re-run converges the role onto exactly what
the file says and refreshes its password — and a changed grant matrix or policy takes effect only
when the script is applied. A feature failing in production with SQLSTATE `42501` means the role is
missing a privilege: add the narrowest grant to the script and re-run it, never `GRANT ALL`. Two
other codes point here rather than at the application: `42501` naming a row-level security policy
means a write tried to land in a budget other than the request's, and `22P02` on an empty-string
`uuid` cast means a connection reached a budget-owned table without an ambient budget on the
session.

## Step 5 — Point the frontend at prod + set OAuth redirect

1. Edit `ClientApp/angular-budgetoid/public/assets/app-config.json`, replacing the placeholders:
   - `apiBaseUrl` → the API URL from Step 2.
   - `auth.google.redirectUri` → the Static Web App URL from Step 1.
   Commit the change.
2. In the **Google Cloud console** → the OAuth 2.0 client → add the Static Web App URL from Step 1
   to **Authorized JavaScript origins** and **Authorized redirect URIs**.

## Step 6 — GitOps: wire the pipeline (one-time)

`.github/workflows/deploy.yml` deploys on every push to `main` (and via manual `workflow_dispatch`).
It needs these GitHub secrets/vars:

| Kind | Name | Value |
|---|---|---|
| secret | `AZURE_STATIC_WEB_APPS_API_TOKEN` | SWA deployment token (Step 1) |
| var | `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | from `azd pipeline config` |
| var | `AZURE_ENV_NAME` / `AZURE_LOCATION` | your azd env name + region |

Run these once, in order:

```sh
# 1) mint the federated (OIDC) app registration + set the AZURE_* GitHub vars automatically
azd pipeline config --provider github

# 2) the SWA token is out-of-band (not an azd concept) — set it manually
gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN \
  --body "$(az staticwebapp secrets list -n budgetoid-web --query 'properties.apiKey' -o tsv)"
```

The pipeline identity needs only management-plane access (`Contributor`) — it does not read Key
Vault, since the connection-string secret is built by the AppHost at deploy time.

After that, pushing to `main` provisions + deploys automatically. You can still trigger a manual
run from the **Actions** tab.

---

## Scale-up ladder (turn these as load / users grow)

1. Cold starts noticeable → set the API `MinReplicas = 1`.
2. Need an uptime SLA on the frontend → SWA **Standard**.
3. DB CPU/IO saturating → move Postgres to **General Purpose**; then add **zone-redundant HA**.
4. Add a **staging** environment + **App Insights** + alerts + a **custom domain**.
5. Automate migrations and role provisioning in the pipeline; revisit **passwordless** DB auth
   (ADR 0001 hardening path).

## Verify (end-to-end)

1. `aspire run` locally still works (dev CORS to `localhost:4200`, local Postgres container).
2. DB: the migration bundle (Step 3) applies cleanly → schema created; the grants script (Step 4)
   applies cleanly → `budgetoid_app` exists with the column grants `\dp` shows.
3. API: `curl https://<api-url>/health` → `200`.
4. Frontend: open the SWA URL, sign in with Google (redirect accepted), create/list/edit/delete a
   transaction — no CORS errors in the browser console.
