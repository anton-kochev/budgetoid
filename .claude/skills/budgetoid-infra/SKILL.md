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

Deletes the env's resource group **including all its data**. Destructive and irreversible — **always confirm with the user before executing**, restating that the env's data is lost. (`rg-budgetoid-shared` is never deleted by this skill.)

1. Confirm with the user.
2. Prod only: disable CI first so a push to `main` can't resurrect the infra (and start billing) while dark: `gh workflow disable deploy.yml`
3. From the repo root: `azd down --purge --force -e "$AZDENV"` (`--purge` is required: without it the soft-deleted Key Vault blocks re-provisioning with the same name for 90 days).
4. Verify the resource group is gone: `az group exists -n "$RG"` → `false`.
5. Remind the user: the local `.azure/` env folders must be **kept** — they store the azd environment config (google-client-id, frontend-origin, subscription, region) that `up` reuses. The `azd pipeline config` OIDC app registration lives in Entra and survives, so CI credentials stay valid.

## Mode: up

Re-provisions from scratch. Follows `DEPLOYMENT.md` Steps 1–4; read it before starting. Takes ~30–60 min, mostly Azure provisioning time.

**Dev only — run from a `develop` checkout.** First time: `az group create -n rg-budgetoid-shared -l northeurope` and `az staticwebapp create -n budgetoid-web-dev -g rg-budgetoid-shared -l westeurope --sku Free`, tell the user to register its hostname in the Google OAuth console (once — it persists), then `azd env new budgetoid-dev` and copy `google-client-id` from `azd env get-values -e budgetoid-prod`, set `frontend-origin` to the dev SWA URL.

1. Prod only: create the Static Web App first (its URL is an `azd up` parameter):
   `az staticwebapp create -n budgetoid-web -g "$RG" -l westeurope --sku Free`
   Capture the default hostname. (If the RG doesn't exist yet, `azd up` in the next step creates it — create the SWA after, then set `frontend-origin` and re-run `azd up`.) Dev reuses its persistent SWA — skip this.
2. `azd up -e "$AZDENV"` from the repo root. With `.azure/` intact it reuses the environment; update `frontend-origin` via `azd env set` if the SWA hostname changed (prod only — dev's is stable).
3. Apply migrations via the EF bundle exactly as in `DEPLOYMENT.md` Step 3 (temporary firewall rule for the current IP → build bundle → run with the Key Vault connection string + `;Ssl Mode=Require` → **delete the firewall rule**). Use this env's Key Vault (find it: `az keyvault list -g "$RG" --query "[0].name" -o tsv`).
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
