# ADR 0001 — Use password authentication for the production PostgreSQL connection

- **Status:** Accepted
- **Date:** 2026-07-11
- **Area:** Deployment / Infrastructure (Azure Database for PostgreSQL Flexible Server)

## Context

Budgetoid is deployed to Azure via `azd` from the Aspire AppHost. The database is an
Azure Database for PostgreSQL Flexible Server (Burstable B1ms), originally provisioned for
**passwordless** authentication (Microsoft Entra / managed identity), with `passwordAuth: Disabled`
and the app's user-assigned managed identity (`mi-…`) added as the Entra administrator.

The **server** side was correct, but the Aspire **client** wiring produced an incomplete
connection string — the Container App received only:

```
Host=postgres-….postgres.database.azure.com;Database=budgetoid
```

No username, no SSL mode, no token. Npgsql therefore defaulted the username to the container's
OS user — `app` (the `noble-chiseled` .NET image runs as `app`) — and connected without SSL.
Azure rejected it: `Npgsql.PostgresException 28000: no pg_hba.conf entry for host "…", user "app",
database "budgetoid", no encryption`. Every DB-touching request 500'd (e.g. `GET /api/currencies`,
user provisioning).

Making passwordless work end-to-end would require **custom Npgsql code** in the API: a periodic
password provider that fetches an Entra access token (`DefaultAzureCredential` scoped to the
user-assigned managed-identity client id) for the `https://ossrdbms-aad.database.windows.net`
resource, plus setting `Username=mi-…` and `Ssl Mode=Require` on the connection string. That is a
real code change and a fragile edge of the Aspire + Azure Postgres integration.

## Decision

Switch the production database connection to **password authentication**. Aspire's
`.WithPasswordAuthentication()` on the Flexible Server generates a strong administrator password,
stores it as an **encrypted Azure Container App secret**, and produces a complete connection
string (user + password + `Ssl Mode=Require`). Migrations run with the same admin credentials.

Rationale: reliable, well-supported, and a password stored in an encrypted secret is standard
production practice. It unblocks the deployment now, versus iterating on custom token-provider code.

## Alternatives considered

- **Fix passwordless in the API (custom Npgsql token provider).** Add a periodic-password-provider
  that returns a managed-identity Entra token, plus username/SSL on the connection string. Rejected
  *for now* — it's custom code on a fragile edge of the Aspire + Azure Postgres integration and
  would likely need several iterations. Not discarded permanently — it's the documented hardening
  path back to passwordless (see below).
- **Keep debugging Aspire's passwordless client wiring as-is.** Rejected — the connection string
  arrived as bare `Host;Database` (no username/SSL/token), which points to a framework gap rather
  than a fixable config value, i.e. an unbounded time sink with no guarantee of resolution.

## Consequences

- The generated password is stored in an **Azure Key Vault** (`postgreskv-…`) that
  `.WithPasswordAuthentication()` provisions automatically — so it is Key-Vault-backed from the
  start, not a plain Container App secret.
- Because the auth model changed, the existing schema (created earlier under an Entra-user owner)
  is dropped and re-migrated under the password admin so ownership/access are correct.
- Slightly less pure than passwordless (no standing secret is the passwordless benefit).

### Two gotchas discovered during rollout (both worked around)

1. **azd does not wire the Key Vault reference into the Container App.** The Aspire manifest
   resolves the app's connection string to `{postgres-kv.secrets.connectionstrings--budgetoid}`
   (a Key Vault reference to the full string), but every `azd deploy` writes a **bare** plain
   secret `Host=…;Database=budgetoid` instead — no username/password. **Workaround:** after each
   `azd deploy`, re-apply the full connection string from Key Vault to the Container App secret and
   restart the revision. This is automated as a post-deploy step in `.github/workflows/deploy.yml`
   and documented in `DEPLOYMENT.md`. A cleaner root fix (construct the connection-string env in
   the AppHost from the password parameter, bypassing the broken reference) is a follow-up.
2. **The connection string carries no `SslMode`.** Azure Postgres requires encryption; without an
   explicit mode Npgsql attempts plaintext and is rejected (`28000 … no encryption`). Fixed in code
   (`Api/Program.cs`): `SslMode=Require` is applied to the connection string in non-Development
   environments (local/test Postgres has no TLS, so dev is left untouched).

## Revisit later (hardening path back to passwordless)

1. Add an Npgsql periodic-password-provider in the API that returns an Entra token from the
   user-assigned managed identity (`ManagedIdentityClientId` from `MANAGED_IDENTITY_CLIENT_ID`),
   scope `https://ossrdbms-aad.database.windows.net/.default`.
2. Set `Username=<managed-identity-name>;Ssl Mode=Require` on the connection string.
3. Re-enable Entra-only auth on the server (`passwordAuth: Disabled`) and drop the password secret.

Tracked as a hardening item alongside the other production follow-ups (Key Vault, staging env,
custom domain, CI-automated migrations).
