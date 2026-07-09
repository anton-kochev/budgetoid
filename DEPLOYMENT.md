# Deploying Budgetoid to Azure

Budgetoid is a **production service**. This baseline starts at the cheapest production-viable
tier (real backups + security, minimal spend) with a clear path to scale up on load.

| Component | Host (start) | Scale-up lever |
|---|---|---|
| API (.NET 10) | Azure Container Apps, consumption, scale-to-zero (`MinReplicas = 0`) | → `MinReplicas = 1` when cold starts bite |
| Frontend (Angular) | Azure Static Web Apps, **Free** (custom domain + TLS included) | → Standard for the SLA |
| PostgreSQL | Azure Postgres **Flexible Burstable B1ms**, 32 GB, 7-day backups + PITR | → General Purpose → zone-redundant HA |
| DB auth | **Passwordless** — API connects via managed identity, no secret stored | — |

**Infra is code.** The Aspire `AppHost` is the source of truth; `azd` provisions **both the API
and the PostgreSQL server** from it (`azd infra synth` writes the Bicep to `./infra` for you to
commit). The **Static Web App is the only out-of-band resource** — created once with one command.

---

## Prerequisites (one-time)

- An Azure subscription + the **Azure Developer CLI** (`azd`) and **Azure CLI** (`az`).
- Access to the **Google Cloud** OAuth client used by the app.
- `dotnet-ef` (`dotnet tool install --global dotnet-ef --version 10.0.0`) for migrations.

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
azd infra synth     # writes Bicep into ./infra  →  git add infra && commit (this is the IaC)
azd up              # provisions ACA + the Postgres Flexible Server, builds/pushes the image, deploys
```

`azd up` prompts for subscription + region, then for two app parameters (wired in the AppHost, so
they land in the committed Bicep — no manual container-app edits):

| Prompt | Value |
|---|---|
| `google-client-id` | your Google OAuth client id |
| `frontend-origin` | the Static Web App URL from Step 1 |

The database needs **no** connection-string prompt — it's provisioned by azd and the API reaches it
passwordlessly via its managed identity. Note the API's public URL from the output.
To change a parameter later: `azd env set <name> <value>` then `azd up`.

## Step 3 — Apply database migrations

Migrations run from the EF bundle, never at API startup. Because the server is passwordless, you
authenticate with a short-lived Microsoft Entra token (no stored password):

```sh
# one-time: make yourself an Entra admin on the server so you can run DDL
az postgres flexible-server ad-admin create \
  -g <resource-group> -s <postgres-server-name> \
  --display-name "<your-email>" --object-id "$(az ad signed-in-user show --query id -o tsv)"

# build the bundle and apply it with an Entra access token as the password
dotnet ef migrations bundle --project BudgetoidApp/Infrastructure \
  --startup-project BudgetoidApp/Api --configuration Release -o ./efbundle
TOKEN="$(az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv)"
./efbundle --connection "Host=<postgres-server-name>.postgres.database.azure.com;Database=budgetoid;Username=<your-email>;Password=$TOKEN;Ssl Mode=Require;Trust Server Certificate=true"
```

> Automating this in CI (granting the pipeline identity DB access + token auth) is a hardening
> follow-up — see the note in `deploy.yml`. Manual application is fine for the first cut.

## Step 4 — Point the frontend at prod + set OAuth redirect

1. Edit `ClientApp/angular-budgetoid/public/app-config.json`, replacing the placeholders:
   - `apiBaseUrl` → the API URL from Step 2.
   - `auth.google.redirectUri` → the Static Web App URL from Step 1.
   Commit the change.
2. In the **Google Cloud console** → the OAuth 2.0 client → add the Static Web App URL from Step 1
   to **Authorized JavaScript origins** and **Authorized redirect URIs**.

## Step 5 — Configure GitHub secrets/vars for CI deploys

`.github/workflows/deploy.yml` (manual `workflow_dispatch` for now) expects:

| Kind | Name | Value |
|---|---|---|
| secret | `AZURE_STATIC_WEB_APPS_API_TOKEN` | SWA deployment token (Step 1) |
| var | `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | from `azd pipeline config` |
| var | `AZURE_ENV_NAME` / `AZURE_LOCATION` | your azd env name + region |

`azd pipeline config` creates the app registration + federated (OIDC) credential and sets these
automatically. After that, run the **Deploy** workflow from the Actions tab (or add
`push: { branches: ["main"] }` to make it GitOps).

---

## Scale-up ladder (turn these as load / users grow)

1. Cold starts noticeable → set the API `MinReplicas = 1`.
2. Need an uptime SLA on the frontend → SWA **Standard**.
3. DB CPU/IO saturating → move Postgres to **General Purpose**; then add **zone-redundant HA**.
4. Add a **staging** environment + **App Insights** + alerts + a **custom domain**.
5. Automate migrations + real **test gates** in the pipeline (needs a frontend test runner).

## Verify (end-to-end)

1. `aspire run` locally still works (dev CORS to `localhost:4200`, local Postgres container).
2. DB: the migration bundle (Step 3) applies cleanly → schema created.
3. API: `curl https://<api-url>/health` → `200`.
4. Frontend: open the SWA URL, sign in with Google (redirect accepted), create/list/edit/delete a
   transaction — no CORS errors in the browser console.
