# Deploying Budgetoid to Azure

Target architecture (all within free tiers → **~$0/mo**):

| Component | Host | Notes |
|---|---|---|
| API (.NET 10) | Azure Container Apps, consumption | scale-to-zero (`MinReplicas = 0`, max 2) |
| Frontend (Angular) | Azure Static Web Apps, free | SSL + custom domain included |
| PostgreSQL | Neon, free serverless | scale-to-zero, external — just a connection string |

Infra is code: the Aspire `AppHost` is the source of truth; `azd` synthesizes committed Bicep
under `infra/`. Neon and the Static Web App are the two out-of-band resources.

The repo is already prepared for this: config-driven CORS (`Cors:AllowedOrigins`), a publish-mode
external `budgetoid` connection string in `AppHost/Program.cs`, a production frontend config with
loader fallback, `azure.yaml`, `staticwebapp.config.json`, and `.github/workflows/deploy.yml`.

---

## Prerequisites (one-time)

- An Azure subscription and the **Azure Developer CLI** (`azd`) + **Azure CLI** (`az`) installed.
- A **Neon** account (neon.tech).
- Access to the **Google Cloud** OAuth client used by the app.
- `dotnet-ef` (`dotnet tool install --global dotnet-ef --version 10.0.0`).

Interactive logins are easiest run from this session with the `!` prefix, e.g. `! azd auth login`.

---

## Step 1 — Create the Neon database

1. In the Neon console, create a project and a database named **`budgetoid`**.
2. Copy the **pooled** connection string (the one with `-pooler` in the host).
3. Append the project pool cap: add `;Maximum Pool Size=5` (or `?...&maxpoolsize=5` depending on
   format). Keep this string handy — it's needed in Steps 3 and 5.

## Step 2 — Create the Static Web App (frontend host)

Create it first so its URL is available when you provision the API (Step 3 prompts for it).

1. Create a **Static Web App** (free plan) in the Azure portal or via CLI. Choose "Other"/manual
   deployment (we deploy from GitHub Actions, not the portal's build).
2. Note the SWA default hostname (e.g. `https://<name>.azurestaticapps.net`) — this is the
   **frontend origin** used in Steps 3, 4 and 5.
3. Copy its **deployment token** (Portal → the SWA → *Manage deployment token*) for Step 5.

## Step 3 — Provision Azure infra + deploy the API

From the repo root:

```sh
azd auth login
azd init            # detects azure.yaml; name the environment e.g. "budgetoid-prod"
azd infra synth     # generate Bicep into ./infra  →  git add infra && commit (this is the IaC)
azd provision       # creates the ACA environment, registry, container app
```

`azd provision` prompts for three deploy-time values (all wired as parameters in the AppHost, so
they're carried in the committed Bicep — no manual container-app edits needed):

| Prompt | Value |
|---|---|
| `budgetoid` connection string | the Neon string from Step 1 |
| `google-client-id` | your Google OAuth client id |
| `frontend-origin` | the Static Web App URL from Step 2 |

Then deploy the image:

```sh
azd deploy          # builds + pushes the chiseled API image and deploys it
```

Note the API's public URL from the output (e.g. `https://api.<hash>.<region>.azurecontainerapps.io`).
To change any parameter later, `azd env set <name> <value>` then re-run `azd provision`.

## Step 4 — Point the frontend at prod + set OAuth redirect

1. Edit `ClientApp/angular-budgetoid/public/app-config.json`, replacing the placeholders:
   - `apiBaseUrl` → the API URL from Step 3.
   - `auth.google.redirectUri` → the Static Web App URL from Step 2.
   Commit the change.
2. In the **Google Cloud console** → the OAuth 2.0 client → add to **Authorized JavaScript
   origins** and **Authorized redirect URIs** the Static Web App URL from Step 2.

## Step 5 — Configure GitHub secrets/vars for CI deploys

`.github/workflows/deploy.yml` (manual `workflow_dispatch`) expects:

| Kind | Name | Value |
|---|---|---|
| secret | `BUDGETOID_DB_CONNECTION` | Neon connection string (Step 1) |
| secret | `AZURE_STATIC_WEB_APPS_API_TOKEN` | SWA deployment token (Step 2) |
| var | `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | from `azd pipeline config` or an app registration |
| var | `AZURE_ENV_NAME` / `AZURE_LOCATION` | your azd env name + region |

The simplest way to wire the Azure OIDC login is `azd pipeline config`, which creates the app
registration + federated credential and sets these automatically. After that, run the **Deploy**
workflow from the Actions tab (or add `push: { branches: ["main"] }` to make it automatic).

The deploy workflow runs the **EF migration bundle** against Neon before deploying the API, so the
schema is applied from CI (never at API startup).

---

## Verify (end-to-end)

1. `aspire run` locally still works (dev CORS to `localhost:4200`, local Postgres container).
2. API: `curl https://<api-url>/health` → `200`; a second call after idle cold-starts from zero.
3. Frontend: open the SWA URL, sign in with Google (redirect accepted), create/list/edit/delete a
   transaction — no CORS errors in the browser console.
4. Cost: confirm the container app is scaled to zero when idle, and Neon usage is ~$0.
