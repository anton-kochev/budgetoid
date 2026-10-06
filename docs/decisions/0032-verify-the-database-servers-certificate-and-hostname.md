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
  resolves to, so the private path still matches. An IP address fails against a certificate issued
  to a DNS name; the privatelink name fails too unless the certificate happens to carry it, which
  nobody has read.
- No `Root Certificate` is set. Npgsql then looks at the `PGSSLROOTCERT` environment variable, then
  `~/.postgresql/root.crt`, and only then the operating system's store — in the API container, the
  base image's `/etc/ssl/certs/ca-certificates.crt`. Neither the image nor the AppHost sets either
  override; both were checked.
- **The remedy for a certificate failure is never a downgrade.** If the server's chain ends in a root
  the image lacks, ship that root in the image and point `Root Certificate` at it.
- Development is untouched: the local Aspire and Testcontainers PostgreSQL images serve no TLS.

**The pipeline proves the chain before it ships the API.** In the database firewall window, the
migrate step asks the SDK which base image it builds `Api.csproj` on (`ComputeContainerBaseImage`;
today `aspnet:10.0-noble-chiseled-extra`, because the project is not invariant-globalization), copies
`ca-certificates.crt` out of that image without running it, and runs
`openssl s_client -starttls postgres -verify_hostname <host> -verify_return_error` against the server
with that file as the only trust anchor. A failure stops the job before `azd deploy`. An `azd up`
from an operator's machine — the first bring-up, or a parameter change — runs neither this check nor
the posture step below. The
provisioning string then connects with `Ssl Mode=VerifyFull` too, so the administrator token is
only ever sent to the server the certificate names.

Held by `DatabaseTransportSecurityTests`, which boots the real composition root in Production,
Staging and Development and reads the mode off the connection the context would open, without
opening it. Every arm was mutated and each mutation reddened a named row.

## Evidence

Measured, not reasoned:

- **The image's trust store.** `aspnet:10.0-noble-chiseled-extra` and `aspnet:10.0-noble-chiseled`,
  pulled while this was written, ship the same bundle byte for byte: 121 certificates. Present: DigiCert Global Root G2 and G3, Microsoft RSA Root CA 2017,
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
`<server>.postgres.database.azure.com`. The check reaches the server over the public endpoint and the
API over the private one; that both present the same certificate is expected and not measured.

## What this extends

Per this repository's convention an ADR is amended by a later ADR and never edited.

- **ADR 0001**, gotcha #2, and **ADR 0007**'s sentence that the API rebuilds the string with
  `SslMode=Require`: the mode is now `VerifyFull`. Their reason — Azure refuses plaintext — still
  holds, and `VerifyFull` encrypts as `Require` did. The trust-store concern that kept validation off
  is answered by the image's store and the pipeline gate, not by skipping validation.
- **ADR 0006**'s sentence that the provisioning string appends `Ssl Mode=Require` for the API's
  reason: it is `VerifyFull` now, for its own reason — it carries an administrator token.
- **ADR 0009**'s statement that the empty-firewall check lives in `DEPLOYMENT.md`'s end-to-end
  checks, and **ADR 0007**'s `passwordAuth: Disabled`: the pipeline's *Verify the database posture*
  step now refuses to ship the API unless the server has no firewall rule, refuses passwords and
  accepts Entra. The manual check remains for every stretch the pipeline does not cover — between
  deploys, and after any `azd up`.

## Consequences

- **A server whose chain ends in a root the image lacks stops a pipeline deploy**, with the host and
  the verify error printed. That is the intended failure: before this, the same server would have
  been accepted, and so would anything impersonating it. It is checked at deploy time only — a
  certificate Azure rotates onto a missing root between deploys fails the API's next connection
  instead, closed.
- **The gate reads the image's store and the provisioning connection reads the runner's.** They are
  both Ubuntu noble `ca-certificates` and are expected to agree; the gate is what makes the API's
  store the one that decides.
- **What the tests cannot see.** A `Root Certificate`, a `PGSSLROOTCERT` or a
  `~/.postgresql/root.crt` pointing at a chosen authority keeps the string reading `VerifyFull` and
  replaces the trust anchor. Each needs configuration or image access, and nothing refuses any of
  them. A `Host=` changed to an IP or the privatelink name
  passes every test and fails at the first connection. Revocation is not checked
  (`Check Certificate Revocation` stays at its default); NFR-010 does not ask for it.
- **The break-glass runbook moves with the pipeline**: its provisioning string uses `VerifyFull`.
  The `psql` recipes keep `sslmode=require`, and that is a **deliberate downgrade**: they send the
  same kind of administrator token unvalidated. It is taken for availability — libpq's `verify-full`
  reads no system store unless told to (`sslrootcert=system`, libpq 16 and later), and a break-glass
  recipe that fails on an operator's machine at the worst moment costs more than the window it
  exposes.
