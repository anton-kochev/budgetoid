# ADR 0032 — Verify the database server's certificate and host name

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Infrastructure / Deployment (Npgsql TLS, API container trust store, deploy pipeline)

## Context

NFR-010 asks that the API validate the database server's certificate chain and host name whenever it
connects outside Development. It did not. `Api/Program.cs` rebuilt the deployed connection string
with `SslMode=Require`, which encrypts and checks nothing: whoever answers on the path can present
any certificate and relay the traffic. The mode answered Azure's refusal of unencrypted connections
([ADR 0001](0001-postgres-password-authentication.md), gotcha #2, carried forward by
[ADR 0007](0007-authenticate-to-postgres-with-managed-identity.md)); the code comment beside it added
that `Require` spared the chiseled container's trust store from needing the server's chain. The
pipeline's provisioning
string copied the option for the same reason
([ADR 0006](0006-automate-migrations-and-provisioning-in-the-pipeline.md)), and that string carries
a live administrator token over the public internet.

There is no production environment while this is decided, so nothing here could be run against the
real server. What was measured, and with what, is listed under *Evidence*.

## Decision

**Outside Development, the API opens PostgreSQL with `SSL Mode=VerifyFull`, and a configured mode
weaker than that refuses boot.**

- `BuildConnectionString` sets `VerifyFull` unconditionally outside Development. An absent key and
  an explicit `Prefer` both end there. An explicit `Prefer` is forced up rather than refused **by
  choice** — the builder's round-tripped string can tell the two apart, but refusing a mode that is
  forced up anyway buys nothing, and nobody writes `Prefer` to get past a certificate error.
- `RefuseWeakSslMode` runs at boot beside `RefuseForbiddenConnectionOptions`, outside Development
  only, and throws on `Disable`, `Allow`, `Require` and `VerifyCA`. It is a separate function
  because the forbidden options are refused in every environment and on both connection strings, and
  this rule is neither. **Refuse, not overwrite**: the likely edit that writes a weak mode is a pasted
  `SslMode=Require` to clear a certificate error at bring-up, and enforcement means rejecting
  ([ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md)). A silent upgrade would hide from
  its author that the paste never took effect. The message names `ConnectionStrings:budgetoid` and
  `SSL Mode` and quotes nothing from the string.
- `VerifyCA` is refused, not tolerated: it checks the chain and skips the name, so any certificate
  any trusted authority ever issued passes.
- **`Host=` stays the server's public FQDN.** Npgsql matches the certificate against the host
  written in the connection string, not the address it resolves to. The private DNS zone
  ([ADR 0009](0009-route-database-traffic-over-a-private-endpoint.md)) changes only what that name
  resolves to, so the private path still matches. An IP address or the privatelink name would fail.
- No `Root Certificate` is set. Npgsql then validates against the operating system's store, which in
  the API container is the base image's `/etc/ssl/certs/ca-certificates.crt`.
- **The remedy for a certificate failure is never a downgrade.** If the server's chain ends in a root
  the image lacks, ship that root in the image and point `Root Certificate` at it.
- Development is untouched: the local Aspire and Testcontainers PostgreSQL images serve no TLS.

**The pipeline proves the chain before anything ships.** In the database firewall window, the
migrate step copies `ca-certificates.crt` out of `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`
— the base the SDK picks for `Api.csproj` — without running the image, and runs
`openssl s_client -starttls postgres -verify_hostname <host> -verify_return_error` against the server
with that file as the only trust anchor. A failure stops the deploy before `azd deploy`. The
provisioning string then connects with `Ssl Mode=VerifyFull` too, so the administrator token is
only ever sent to the server the certificate names.

Held by `DatabaseTransportSecurityTests`, which boots the real composition root in Production,
Staging and Development and reads the mode off the connection the context would open, without
opening it. Every arm was mutated and each mutation reddened a named row.

## Evidence

Measured, not reasoned:

- **The image's trust store.** `aspnet:10.0-noble-chiseled`, pulled while this was written, ships
  121 certificates in one bundle. Present: DigiCert Global Root G2 and G3, Microsoft RSA Root CA 2017,
  Microsoft ECC Root CA 2017. Absent: DigiCert Global Root CA (G1) and Baltimore CyberTrust Root.
  The tag floats, so this is a fact about one pull, which is why the pipeline re-reads it every
  deploy rather than trusting this paragraph.
- **Npgsql 10.0.3 inside that image, against PostgreSQL 17 with a throwaway CA.** With the stock
  store, `Require` connects and `VerifyCA` and `VerifyFull` fail. With the test CA added and the host
  named as on the certificate, all three connect. Reached through a second name, `VerifyCA` connects
  and `VerifyFull` fails — so the name checked is the one written in the string.
- **Validation cannot be switched off beside `VerifyFull`.** Npgsql throws `ArgumentException` when
  `VerifyFull` is combined with a user certificate-validation callback or with an SSL-options
  callback that overrides the remote-certificate check, and `Trust Server Certificate=true` under
  `VerifyFull` still rejects an untrusted certificate. The mode is therefore the switch, and the mode
  is what the tests pin.
- **The `openssl` gate fails closed.** OpenSSL 3.0.13 on Ubuntu 24.04 against a self-signed server
  exits 1 with `Verify return code: 18`. Against the throwaway CA it reports `20` for an unknown
  issuer, `62` for a name mismatch and `0` when both hold.

Not measured, and settled by the first deploy's `openssl` step: which root Azure Database for
PostgreSQL Flexible Server's chain ends in today, and that its certificate names
`<server>.postgres.database.azure.com`.

## What this extends

Per this repository's convention an ADR is amended by a later ADR and never edited.

- **ADR 0001**, gotcha #2, and **ADR 0007**'s sentence that the API rebuilds the string with
  `SslMode=Require`: the mode is now `VerifyFull`. Their reason — Azure refuses plaintext — still
  holds, and `VerifyFull` encrypts as `Require` did. The trust-store concern that kept validation off
  is answered by the image's store and the pipeline gate, not by skipping validation.
- **ADR 0006**'s sentence that the provisioning string appends `Ssl Mode=Require` for the API's
  reason: it is `VerifyFull` now, for its own reason — it carries an administrator token.

## Consequences

- **A server whose chain ends in a root the image lacks stops the deploy**, in the pipeline, with the
  host and the verify error printed. That is the intended failure: before this, the same server
  would have been accepted, and so would anything impersonating it.
- **The gate reads the image's store and the provisioning connection reads the runner's.** They are
  both Ubuntu noble `ca-certificates` and are expected to agree; the gate is what makes the API's
  store the one that decides.
- **What the tests cannot see.** A `Root Certificate` pointing at a chosen authority keeps the
  string reading `VerifyFull` and replaces the trust anchor; it needs both configuration access and a
  file inside the image, and nothing refuses it. A `Host=` changed to an IP or the privatelink name
  passes every test and fails at the first connection. Revocation is not checked
  (`Check Certificate Revocation` stays at its default); NFR-010 does not ask for it.
- **The break-glass runbook moves with the pipeline**: its provisioning string uses `VerifyFull`.
  The `psql` recipes keep `sslmode=require`, because libpq's `verify-full` reads no system store
  unless told to (`sslrootcert=system`, libpq 16 and later), and a recipe that fails on an
  operator's machine at the worst moment is not a security gain.
