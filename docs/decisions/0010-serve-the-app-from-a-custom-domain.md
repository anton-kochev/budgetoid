# ADR 0010 — Serve the app from a custom domain

- **Status:** Accepted — **not yet implemented.** The deployment still answers on its generated Azure
  hostnames; nothing below is live. `DEPLOYMENT.md` Step 6 is the cutover.
- **Date:** 2026-07-31
- **Area:** Deployment / DNS (domain registration, authoritative DNS, Azure Static Web Apps and Azure
  Container Apps custom domains, Google OAuth client configuration)

## Context

Both public hostnames are generated, and neither is stable.

The API answers on `api.<adjective><noun>-<hex>.northeurope.azurecontainerapps.io`, where the middle
segment is the DNS suffix minted by the Container Apps environment. [ADR
0009](0009-route-database-traffic-over-a-private-endpoint.md) established that a virtual network can
be attached to that environment only at creation, which made the AppHost take ownership of it, which
means any change touching the environment's identity mints a **new suffix**. The frontend is the
same story with a different random generator: Static Web Apps assigns
`<adjective>-<noun>-<hex>.azurestaticapps.net` at creation.

Those two strings are not confined to infrastructure. They are written into
`ClientApp/angular-budgetoid/public/assets/app-config.json` as `apiBaseUrl` and `redirectUri`, into
the `frontend-origin` azd parameter that becomes the API's `Cors__AllowedOrigins__0`, and — the part
that hurts — into the **Google OAuth client**, whose authorized JavaScript origins and redirect URIs
live in a web console outside this repository, outside the pipeline, and outside anything that can
fail a build.

So a hostname change is currently a four-place edit, one of which no automated check can see. The
repository already carries the scar: commit `10e0bb9`, *"the frontend calls an api hostname the
rebuild replaced"*. ADR 0009 named this cost when it accepted environment ownership and called it
*"the strongest argument against ever recreating the environment casually"*. It is worth being exact
about what that cost is. It is not the edit — four edits are ten minutes. It is that the deployment
can be **half-migrated and look healthy**: the API is up, the frontend loads, and login is broken,
because the only piece that was missed lives in a console nobody diffed.

A custom domain does not make the generated hostnames stable. It makes them **private**. Once the
public identity is a name this project controls, a regenerated Azure hostname is a DNS record edit
and nothing else — no application config, no OAuth client, no commit.

That the app should eventually have one was already written down, as item 4 of `DEPLOYMENT.md`'s
scale-up ladder, filed alongside staging and alerting. Being cheap to do is not what promotes it: the
private-endpoint work is what turned "nice URL" into "the layer of indirection that makes environment
recreation survivable".

## Decision

**The app is served from `budgetoid.app`, registered at Cloudflare Registrar, with Cloudflare as
authoritative DNS, and DNS resolution only — no proxying.**

**The apex serves the frontend; the API gets one subdomain.**

| Name | Target | Record |
|---|---|---|
| `budgetoid.app` | Static Web App | `CNAME` to the SWA hostname, flattened at the apex |
| `api.budgetoid.app` | Container App | `CNAME` to the container app FQDN, plus the `asuid` `TXT` |
| `www.budgetoid.app` | — | redirect to the apex |

The apex is the shortest thing a user can type and the least that has to be explained. `api.` mirrors
the label Azure already puts on the container app, so the subdomain reads the same in the DNS zone as
it does in the portal. A `CNAME` at a zone apex is not legal DNS; Cloudflare's CNAME flattening
answers with the resolved address, which is what makes the apex reachable without an `A` record
pinned to an address Azure does not promise to keep.

**Cloudflare Registrar sells at the registry's wholesale price with no markup** — roughly $14/year
for `.app`, at registration and at every renewal, with no first-year discount to be re-priced later.
The condition attached is that the domain's authoritative DNS must stay with Cloudflare; the
registrar does not permit external nameservers. That is a genuine coupling and it is accepted, for
the reason under *Alternatives*: the DNS this project needs is four records, and Azure DNS bills
per-zone and per-query to provide the same four.

**DNS-only, not proxied.** Cloudflare answers with Azure's address and steps out of the path. The
three things the proxy provides do not currently apply: Static Web Apps already serves the frontend
from its own edge network, so the cache fronts a cache; every API route requires a valid Google token
and every budget-owned row is behind a row-level security policy ([ADR
0005](0005-isolate-budget-owned-rows-with-row-level-security.md)), so the WAF guards a locked door;
and proxying would replace the client address in Azure's logs with Cloudflare's, degrading
observability until `CF-Connecting-IP` is explicitly handled.

Against that, the proxy is a participant in every request that can fail independently of Azure — and
with `MinReplicas = 0`, cold starts meet Cloudflare's 100-second edge timeout in exactly the way that
produces an error attributable to neither party. This is the one part of this record that is a
**toggle rather than a fork**: the registrar is a multi-year commitment, the orange cloud is a click.
It gets turned on the day there is a specific reason — edge rate limiting, observed abuse — and not
before.

## What `.app` requires

`.app` is on the HSTS preload list **at the TLD level**. Every major browser ships with the rule
compiled in, so `http://budgetoid.app` is never requested; the browser rewrites it to HTTPS before a
packet leaves. This is a security property worth having and it removes a step from the cutover — no
HTTP-to-HTTPS redirect needs configuring, because there is no HTTP.

It also removes the safety net. There is no plaintext fallback, no certificate warning to click
through, and no degraded mode. A domain whose certificate has not been issued yet is not a slow site;
it is an unreachable one. The ordering in the runbook — bind the domain in Azure, wait for the
managed certificate, *then* move the DNS record — is therefore not tidiness. It is the only ordering
that has a working state at both ends.

The same logic applies to the registration itself. **Auto-renew must be on.** An expired `.app` is a
total outage with no partial failure to notice first.

## Alternatives considered

- **Keep the generated Azure hostnames.** Rejected. It is free and it works, and it makes every
  future environment recreation a four-place edit with one place that no build, test, or deploy can
  verify. ADR 0009 accepted environment recreation as a documented procedure; this is what makes the
  procedure cheap enough for that acceptance to hold.
- **A third-party registrar with Azure DNS hosting the zone.** Rejected on cost with no compensating
  benefit. Azure DNS bills per zone per month plus per million queries to serve a zone with four
  records; Cloudflare serves it free. Keeping DNS "inside Azure" would be an argument if anything in
  the AppHost managed records, and nothing does — the zone is edited by hand a handful of times in
  its life. The coupling to Cloudflare is real but its escape hatch is ordinary: transfer the domain
  out, point the nameservers elsewhere. Nothing in this repository would change.
- **Azure App Service Domains.** Rejected on availability before preference: it does not offer
  `.app`. It is also GoDaddy-backed and more expensive, so the only thing it would have bought is one
  fewer vendor on the invoice.
- **Cloudflare with the proxy enabled from day one.** Rejected as premature rather than wrong; see
  the *Decision*. Worth stating plainly is the cutover hazard it carries even when it is eventually
  wanted: both Azure services validate a custom domain by resolving it and checking what answers, and
  a proxied record answers with Cloudflare's address. Validation must happen DNS-only, and the proxy
  goes on afterward — never the reverse.
- **A `.com`.** Rejected. `budgetoid.com` would cost less and carry no HSTS-preload requirement, and
  `.app` is the more honest label for what this is. The preload rule is a feature for a service that
  handles financial data, not a tax.

## Consequences

- **The generated hostnames stop being public API and become implementation detail.** After cutover,
  `app-config.json` names `https://api.budgetoid.app`, the `frontend-origin` azd parameter and its
  `Cors__AllowedOrigins__0` name `https://budgetoid.app`, and the Google OAuth client names the apex.
  A recreated Container Apps environment then costs one `CNAME` edit. The console step that no check
  can see is spent once, here, instead of on every recreation.
- **The `frontend-origin` parameter must be updated in the azd environment, not only in the repo.**
  It lives in `.azure/budgetoid-prod/.env` as `AZURE_FRONTEND_ORIGIN` and is what the next `azd
  provision` bakes into the container app. A cutover that edits `app-config.json` and forgets this
  one produces a frontend on the new domain and an API that rejects its requests with a CORS error —
  which reads in the browser as a network failure, not as a configuration mistake.
- **Two custom domains means two managed certificates, each renewing on its own.** Azure renews both
  automatically; the failure mode is silent until the browser refuses the site outright, because
  `.app` has no degraded state. This belongs in the same monitoring as the rest of the deployment
  rather than in a calendar reminder.
- **`www` needs a record even though nothing serves it.** With no `www` in the zone, `www.budgetoid.app`
  is `NXDOMAIN` — an error page, not a redirect. It is a redirect rule at Cloudflare, not a second
  Azure binding.
- **The domain is now the single point of failure the deployment did not previously have.** Every
  hostname resolves through one zone at one registrar under one account. That account's credentials
  are worth what the deployment is worth, which is an argument for its own hardening
  (multi-factor, no shared access) and not a reason to reconsider the decision — the alternative
  was three vendors with the same property.
- **Nothing in the backend learns about this.** No code path, no environment branch, no test. The
  domain is DNS, two Azure bindings, and one console edit; the application answers on whatever host
  reaches it.
