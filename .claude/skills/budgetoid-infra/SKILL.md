---
name: budgetoid-infra
description: "Manage the Budgetoid Azure prod infrastructure lifecycle to control costs. Modes: pause (stop Postgres compute), resume (restart Postgres), down (delete all infra), up (re-provision from scratch). Use when the user wants to pause, stop, shut down, tear down, delete, resume, restart, or recreate the Azure infrastructure, or asks about stopping Azure charges."
argument-hint: pause | resume | down | up
disable-model-invocation: true
---

# Budgetoid Infra Lifecycle

Cost-control lifecycle for the Azure prod environment (`rg-budgetoid-prod`, azd env `budgetoid-prod`, region `northeurope`). The mode is given as the argument. If no mode is given, show the current state (Step 0 of `pause`) and the four modes, then stop.

Background: infra is provisioned by `azd` from the Aspire AppHost (see `DEPLOYMENT.md`). The only meaningful idle cost is the Postgres Flexible Server (~$13–17/mo compute); the Container App already scales to zero, the Static Web App is Free tier, ACR Basic is ~$5/mo and cannot be paused.

Resolve resource names dynamically — never hardcode the random suffix:

```sh
PG=$(az postgres flexible-server list -g rg-budgetoid-prod --query "[0].name" -o tsv)
```

## Mode: pause

Stops Postgres compute. Storage (~$4/mo) and ACR (~$5/mo) keep billing; everything else is ~$0 idle.

1. Show current state first: `az postgres flexible-server show -g rg-budgetoid-prod -n "$PG" --query state -o tsv`. If already `Stopped`, say so and stop.
2. `az postgres flexible-server stop -g rg-budgetoid-prod -n "$PG"`
3. Warn the user about both traps:
   - **Azure auto-restarts a stopped flexible server after 7 days** (hard platform limit). Offer to set up a weekly re-stop routine if they want it off longer.
   - **A push to `main` runs `azd provision`, whose desired state is "running"** — it may restart the DB. Offer `gh workflow disable deploy.yml` (remind them to re-enable on resume).

The API will 500 on DB-touching requests while paused; the frontend still serves. That's expected.

## Mode: resume

1. `az postgres flexible-server start -g rg-budgetoid-prod -n "$PG"` (no-op message if already `Ready`).
2. Re-enable CI if it was disabled: `gh workflow enable deploy.yml`.
3. Verify: `curl -fsS https://$(az containerapp show -g rg-budgetoid-prod -n api --query properties.configuration.ingress.fqdn -o tsv)/health` → expect `200` (first hit may be slow: cold start + DB warmup; retry once).

## Mode: down

Deletes **everything** including all data. Destructive and irreversible — **always confirm with the user before executing**, restating that all data is lost.

1. Confirm with the user.
2. Disable CI first so a push to `main` can't resurrect the infra (and start billing) while dark: `gh workflow disable deploy.yml`
3. From the repo root: `azd down --purge --force` (`--purge` is required: without it the soft-deleted Key Vault blocks re-provisioning with the same name for 90 days).
4. Verify the resource group is gone: `az group exists -n rg-budgetoid-prod` → `false`.
5. Remind the user: local `.azure/` env folder must be **kept** — it stores the azd environment (google-client-id, frontend-origin, subscription, region) that `/infra up` reuses. The `azd pipeline config` OIDC app registration lives in Entra and survives, so CI credentials stay valid.

## Mode: up

Re-provisions from scratch. Follows `DEPLOYMENT.md` Steps 1–4; read it before starting. Takes ~30–60 min, mostly Azure provisioning time. Fresh hostnames are issued, so URL housekeeping at the end is mandatory.

1. Create the Static Web App first (its URL is an `azd up` parameter):
   `az staticwebapp create -n budgetoid-web -g rg-budgetoid-prod -l westeurope --sku Free`
   Capture the default hostname. (If the RG doesn't exist yet, `azd up` in the next step creates it — create the SWA after, then set `frontend-origin` and re-run `azd up`.)
2. `azd up` from the repo root. With `.azure/` intact it reuses the environment; update the frontend origin if the SWA hostname changed: `azd env set frontend-origin <new SWA URL>`.
3. Apply migrations via the EF bundle exactly as in `DEPLOYMENT.md` Step 3 (temporary firewall rule for the current IP → build bundle → run with the Key Vault connection string + `;Ssl Mode=Require` → **delete the firewall rule**).
4. URL housekeeping (new random hostnames on both ends):
   - Update `apiBaseUrl` and `auth.google.redirectUri` in `ClientApp/angular-budgetoid/public/assets/app-config.json`; commit.
   - Tell the user to update the Google OAuth client (authorized JS origins + redirect URIs) in the Google Cloud console — this is manual, they must do it.
   - Refresh the SWA deployment token secret:
     `gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN --body "$(az staticwebapp secrets list -n budgetoid-web --query 'properties.apiKey' -o tsv)"`
5. Re-enable CI: `gh workflow enable deploy.yml`, then push (or `gh workflow run deploy.yml`) to deploy the frontend with the updated config.
6. Verify end-to-end: API `/health` returns `200`; open the SWA URL, sign in with Google, create/list a transaction.

## Rules

- Report each step's outcome as you go; on any Azure CLI error, stop and show the full error instead of continuing.
- If `DEPLOYMENT.md` and this skill disagree, `DEPLOYMENT.md` wins — flag the drift so the skill gets updated.
