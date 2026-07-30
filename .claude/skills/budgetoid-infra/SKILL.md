---
name: budgetoid-infra
description: "Manage the Budgetoid Azure infrastructure lifecycle (prod and dev environments) to control costs. Modes: pause (stop Postgres compute), resume (restart Postgres), down (delete all infra), up (re-provision from scratch). Use when the user wants to pause, stop, shut down, tear down, delete, resume, restart, or recreate the Azure infrastructure, or asks about stopping Azure charges."
argument-hint: pause | resume | down | up [prod|dev]
disable-model-invocation: true
---

# Budgetoid Infra Lifecycle

Cost-control lifecycle for the Azure environments. Arguments: `<mode> [env]`, env defaults to `prod`. If no mode is given, show the current state (Step 0 of `pause`) for both envs and the four modes, then stop.

| env | azd environment | resource group | Static Web App |
|---|---|---|---|
| `prod` | `budgetoid-prod` | `rg-budgetoid-prod` | `budgetoid-web` (in the env RG) |
| `dev` | `budgetoid-dev` | `rg-budgetoid-dev` | `budgetoid-web-dev` (in `rg-budgetoid-shared`, **persistent**) |

**Never touch `rg-budgetoid-msi`.** It holds `msi-budgetoid`, the user-assigned managed identity the
deploy pipeline authenticates as, and it sits outside every env's resource group on purpose: that
identity is what runs `azd provision`, so tearing an environment down must not take with it the thing
that rebuilds it. It is a resource, not an Entra app registration — a `down` that reaches it destroys
it, and user-assigned identities cannot be moved or restored, only recreated with a new client id.
Both persistent groups (`rg-budgetoid-msi`, `rg-budgetoid-shared`) are out of scope for every mode
here.

Set once per invocation and use throughout: `ENV=<env>`, `AZDENV=budgetoid-$ENV`, `RG=rg-budgetoid-$ENV`. Pass `-e "$AZDENV"` to every azd command — never rely on the currently selected env.

Background: infra is provisioned by `azd` from the Aspire AppHost (see `DEPLOYMENT.md`). The only meaningful idle cost per env is the Postgres Flexible Server (~$13–17/mo compute); the Container App already scales to zero, Static Web Apps are Free tier, ACR Basic is ~$5/mo and cannot be paused. Each env gets its own Postgres — never point dev at the prod server. **Dev is ephemeral**: bring it `up` from a `develop` checkout for a testing session, tear it `down` after (per-hour billing makes a session cost cents). Its SWA is the exception — it lives in `rg-budgetoid-shared`, survives teardown, and keeps a stable hostname so its Google OAuth registration stays valid.

Resolve resource names dynamically — never hardcode the random suffix:

```sh
PG=$(az postgres flexible-server list -g "$RG" --query "[0].name" -o tsv)
```

## Mode: pause

Stops Postgres compute. Storage (~$4/mo) and ACR (~$5/mo) keep billing; everything else is ~$0 idle. (Pausing dev is usually wrong — tear it down instead.)

1. Show current state first: `az postgres flexible-server show -g "$RG" -n "$PG" --query state -o tsv`. If already `Stopped`, say so and stop.
2. `az postgres flexible-server stop -g "$RG" -n "$PG"`
3. Warn the user about both traps:
   - **Azure auto-restarts a stopped flexible server after 7 days** (hard platform limit). Offer to set up a weekly re-stop routine if they want it off longer.
   - Prod only: **a push to `main` runs `azd provision`, whose desired state is "running"** — it may restart the DB. Offer `gh workflow disable deploy.yml` (remind them to re-enable on resume).

The API will 500 on DB-touching requests while paused; the frontend still serves. That's expected.

## Mode: resume

1. `az postgres flexible-server start -g "$RG" -n "$PG"` (no-op message if already `Ready`).
2. Prod only: re-enable CI if it was disabled: `gh workflow enable deploy.yml`.
3. Verify: `curl -fsS https://$(az containerapp show -g "$RG" -n api --query properties.configuration.ingress.fqdn -o tsv)/health` → expect `200` (first hit may be slow: cold start + DB warmup; retry once).

## Mode: down

Deletes the env's resource group **including all its data**. Destructive and irreversible — **always confirm with the user before executing**, restating that the env's data is lost. (`rg-budgetoid-shared` and `rg-budgetoid-msi` are never deleted by this skill.)

1. Confirm with the user.
2. Prod only: disable CI first so a push to `main` can't resurrect the infra (and start billing) while dark: `gh workflow disable deploy.yml`
3. From the repo root: `azd down --purge --force -e "$AZDENV"`. Keep `--purge`: a soft-deleted resource left by an earlier deployment (a Key Vault from the password-auth era) blocks re-provisioning under the same name for 90 days, and the flag is a no-op when there is none.
4. Verify the resource group is gone: `az group exists -n "$RG"` → `false`, and that the identity group is **still there**: `az group exists -n rg-budgetoid-msi` → `true`.
5. Remind the user: the local `.azure/` env folders must be **kept** — they store the azd environment config (google-client-id, frontend-origin, subscription, region, and the pipeline principal identifiers) that `up` reuses.

The pipeline identity survives only because it lives in `rg-budgetoid-msi`. If a teardown ever does
destroy it — the symptom is `azd auth login` failing in CI, or `az ad sp show --id "$AZURE_CLIENT_ID"`
finding nothing — recovery is `azd pipeline config --provider github --auth-type federated`, then
re-deriving both principal variables from the new client id (see `up`, prerequisites). Expect that
command to commit and push a generated `azure-dev.yml` workflow on its own, even with `--no-prompt`;
delete it afterwards, it conflicts with `deploy.yml`.

## Mode: up

Re-provisions from scratch. Follows `DEPLOYMENT.md` Steps 1–4; read it before starting. Takes ~30–60 min, mostly Azure provisioning time.

**Prerequisites — check before anything else.** The Postgres server is provisioned Entra-only, and the
Bicep builds an Entra administrator resource from two identifiers, so `azd` fails at bicep
initialisation ("missing required inputs") before it creates a thing if either is absent:

```sh
CID=$(gh variable list --json name,value -q '.[]|select(.name=="AZURE_CLIENT_ID").value')
az ad sp show --id "$CID" --query "{objectId:id, displayName:displayName}" -o json
```

The object id and display name go into `AZURE_PIPELINE_PRINCIPAL_ID` / `_NAME`, in **both**
`gh variable set` and `azd env set -e "$AZDENV"` — azd reads the local env, the workflow reads the
repository variables. If `az ad sp show` finds nothing, the pipeline identity is gone: recover it
first (see `down`). `msi-budgetoid` serves every environment; there is no per-env identity.

**Dev only — run from a `develop` checkout.** First time: `az group create -n rg-budgetoid-shared -l northeurope` and `az staticwebapp create -n budgetoid-web-dev -g rg-budgetoid-shared -l westeurope --sku Free`, tell the user to register its hostname in the Google OAuth console (once — it persists), then `azd env new budgetoid-dev` and copy `google-client-id` from `azd env get-values -e budgetoid-prod`, set `frontend-origin` to the dev SWA URL.

1. Prod only: create the Static Web App first (its URL is an `azd up` parameter):
   `az staticwebapp create -n budgetoid-web -g "$RG" -l westeurope --sku Free`
   Capture the default hostname. (If the RG doesn't exist yet, `azd up` in the next step creates it — create the SWA after, then set `frontend-origin` and re-run `azd up`.) Dev reuses its persistent SWA — skip this.
2. `azd up -e "$AZDENV"` from the repo root. With `.azure/` intact it reuses the environment; update `frontend-origin` via `azd env set` if the SWA hostname changed (prod only — dev's is stable). Expect the API to be entirely down until step 3 finishes, not merely short of tables: `azd up` deploys the container before the database work, and until the role is bound to its identity the API cannot authenticate to Postgres at all. `deploy.yml` orders it correctly; `azd up` cannot.
3. Migrate the schema, provision the `budgetoid_app` role, and bind it to the API's identity — one tool, `Tools/DbProvision`, exactly as in `DEPLOYMENT.md` Step 3. There is no stored credential to look up: the admin password is a Microsoft Entra access token minted on the spot (`az account get-access-token --resource-type oss-rdbms`). The firewall dance is unchanged — open a rule for the current IP, run, **delete the rule**.
   - **Prod: prefer letting the pipeline do it** — `gh workflow run deploy.yml` runs migrate → provision → verify → bind in the designed order. The pipeline identity is the *only* Entra administrator the Bicep registers, so it is the only principal that can label a role as an Entra principal.
   - Running it by hand means adding yourself as an administrator first, because your user account is not one: `az postgres flexible-server ad-admin create -g "$RG" -s "$PG" -u "$(az ad signed-in-user show --query userPrincipalName -o tsv)" -i "$(az ad signed-in-user show --query id -o tsv)" -t User`. Required for dev, which has no pipeline.
4. Frontend config (new API hostname every recreation):
   - Prod: update `apiBaseUrl` and `auth.google.redirectUri` in `ClientApp/angular-budgetoid/public/assets/app-config.json`; commit. Tell the user to update the Google OAuth client (authorized JS origins + redirect URIs) — manual, they must do it. Refresh the SWA deployment token secret:
     `gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN --body "$(az staticwebapp secrets list -n budgetoid-web --query 'properties.apiKey' -o tsv)"`
   - Dev: edit `app-config.json` locally with the dev API URL + dev SWA redirect — **do not commit**; build and deploy manually:
     `npx -y @azure/static-web-apps-cli deploy ClientApp/angular-budgetoid/dist/angular-budgetoid/browser --env production --deployment-token "$(az staticwebapp secrets list -n budgetoid-web-dev --query 'properties.apiKey' -o tsv)"` (run the production build first; revert `app-config.json` after). No Google console change needed — the dev hostname was registered once at first setup.
5. Prod only: re-enable CI: `gh workflow enable deploy.yml`, then push (or `gh workflow run deploy.yml`) to deploy the frontend with the updated config.
6. Verify end-to-end: API `/health` returns `200`; open the env's SWA URL, sign in with Google, create/list a transaction.

## Rules

- Report each step's outcome as you go; on any Azure CLI error, stop and show the full error instead of continuing.
- If `DEPLOYMENT.md` and this skill disagree, `DEPLOYMENT.md` wins — flag the drift so the skill gets updated.
