# ADR 0009 — Route database traffic over a private endpoint

- **Status:** Accepted
- **Date:** 2026-07-30
- **Area:** Deployment / Networking (virtual network, private endpoint, private DNS, Azure Database
  for PostgreSQL Flexible Server firewall, Azure Container Apps environment)

## Context

[ADR 0006](0006-automate-migrations-and-provisioning-in-the-pipeline.md) rejected an alternative in
terms that left no room to interpret: *"A permanent 'allow all Azure services' firewall rule instead
of a transient one. Rejected: that rule admits every Azure tenant's egress, forever, in exchange for
removing two steps from one workflow. The transient rule admits one address for a couple of minutes
and deletes itself. The comparison is not close enough to be a trade-off."*

**That exact rule was on the server while the sentence was being written.** Aspire's PostgreSQL
hosting package adds a firewall rule named `AllowAllAzureIps` — start and end address `0.0.0.0`,
which is Azure's encoding of "any Azure-hosted caller" — to every server it generates, and exposes no
public API to decline it. The deployment's real posture and its documented posture were therefore
different, and the difference was invisible from inside the repository: nothing in the AppHost
mentioned a firewall rule, nothing in `DEPLOYMENT.md` did, and the generated Bicep is not checked in.
Naming that plainly is part of this record's job. The defect was not that a permissive rule existed —
a hosting package added it and that is a reasonable default for a resource most people reach from
other Azure services. The defect was that a design whose whole value is being written down had no way
of noticing that one of its written-down claims was false.

The rule was also, until this decision, load-bearing. Azure Container Apps on a Consumption workload
profile with no virtual network egresses from shared platform addresses that Microsoft does not
guarantee to be stable. Deleting the rule and replacing it with a narrow one naming the environment's
current outbound address would have worked, and would have kept working, until an unannounced
platform change moved that address — at which point the API stops reaching its database in
production, with no deploy and no code change to point at.

So the rule could not simply be removed. Something had to take over the job it was doing, and that
something is a path that does not traverse the public internet at all.

## What this extends

Per this repository's convention an ADR is amended by a later ADR and never edited, so the passages
this record changes are named here rather than corrected in place.

- **[ADR 0006](0006-automate-migrations-and-provisioning-in-the-pipeline.md) — the transient
  firewall window stands and finally becomes true.** Its mechanism is unchanged: the runner resolves
  its public IP, creates `gh-deploy-migration` for that one address, and deletes it in an
  `if: always()` step. What changes is that the window is now the only way in rather than the
  narrowest of several. Its rejection of the permanent allow-all rule is not superseded; it is
  implemented, one record late.
- **[ADR 0006](0006-automate-migrations-and-provisioning-in-the-pipeline.md) — the deploy workflow's
  scale-to-zero claim is superseded.** The workflow carried an `az containerapp update
  --min-replicas 0` step justified by the AppHost having no say over the generated container app.
  That was true only while azd owned the Container Apps environment. It does not own it any more, and
  the setting moves into the app model.
- **[ADR 0007](0007-authenticate-to-postgres-with-managed-identity.md) — untouched, and it is what
  makes this decision an improvement rather than a rescue.** Entra-only authentication means the
  allow-all rule granted *reachability* and never *access*: there is no password to guess and a token
  cannot be forged. Nothing was exposed by it. This record is about removing a layer, not about
  closing a breach, and the distinction is worth keeping straight when reading the alternatives.

## Decision

**The API reaches PostgreSQL over a private endpoint on a virtual network. The server carries no
standing firewall rule. Public network access stays enabled solely so the deploy pipeline can open
its transient single-address window.**

**One virtual network, `10.0.0.0/16`, with two subnets.** `container-apps` is a `/27` delegated to
`Microsoft.App/environments` — `/27` is the documented minimum for a workload-profiles environment,
and an undelegated subnet is refused at environment creation rather than at first use.
`private-endpoints` is a `/28` with no delegation, because a private endpoint consumes one address
and `/28` is the smallest subnet Azure accepts. The network is a standalone infrastructure module
rather than a callback on either consumer: the environment needs the delegated subnet and the
database needs the endpoint subnet and the DNS zone, so nesting the network inside either would make
the other depend on it for nothing.

**A private endpoint on the server (`groupIds: ['postgresqlServer']`), a private DNS zone
`privatelink.postgres.database.azure.com`, a link from that zone to the network, and a DNS zone group
on the endpoint.** The four pieces are one mechanism and none of them works alone: the endpoint
allocates the private address, the zone holds the record, the zone group writes the server's record
into the zone, and the link is what makes resolvers inside the network consult it.

**The connection string does not change, and that is the point rather than a convenience.** The
server's ordinary public FQDN resolves to the private address inside the network and to the public
one everywhere else, because DNS resolution is the only thing the zone group and the link alter.
Nothing in the application, nothing in `DbProvision`, and no documented command had to learn that
private networking exists. A design whose correctness depends on every caller remembering to use a
different hostname is a design with a failure mode in every future caller; this one has none.

**`publicNetworkAccess` stays `Enabled`, deliberately.** The deploy pipeline runs on a GitHub-hosted
runner, which has no route into the network and cannot be given one without machinery an order of
magnitude larger than this decision. Disabling public access would take migrations, grants, policies
and coverage verification with it, and would contradict ADR 0006's mechanism outright. What changes
is not whether the public endpoint exists but whether anything is admitted through it between
deploys: nothing is.

**The allow-all rules are removed by deleting the provisionables from the generated infrastructure.**
`infrastructure.GetProvisionableResources().OfType<PostgreSqlFlexibleServerFirewallRule>()` fed to
`Infrastructure.Remove`, because Aspire offers no way to ask it not to add them. Matching by type and
removing every match is chosen over removing a fixed count, so that a hosting package which starts
emitting a different set of rules produces a template with none of them rather than a template with
whichever ones survived an off-by-one.

## The ownership consequence

A virtual network can be attached to a Container Apps environment **only at creation**. The
environment was generated by azd, which meant nothing in the AppHost could reach its network
configuration at all. So the AppHost takes ownership —
`AddAzureContainerAppEnvironment("cae").WithAzdResourceNaming()` — and taking ownership means the
environment is created afresh. The costs are not incidental and belong in the record:

- **The API gets a new hostname.** A Container Apps environment mints a new DNS suffix each time it
  is created, so `app-config.json` and the Google OAuth client's authorized JavaScript origins and
  redirect URIs all need updating in step with the deploy. This is a recurring cost of every future
  environment recreation, not a one-time migration chore, and it is the strongest argument against
  ever recreating the environment casually.
- **`WithAzdResourceNaming()` is load-bearing.** It keeps the container registry, the Log Analytics
  workspace and the managed identity on azd's naming scheme, so the registry that already holds the
  application's images stays the registry the deploy pushes to. Without it those resources are
  renamed alongside the environment and the first deploy starts from an empty registry.
- **Scale-to-zero moves into the app model.** `PublishAsAzureContainerApp` with
  `MinReplicas = 0, MaxReplicas = 2` replaces the `az containerapp update` workflow step, so the
  setting is applied by the same deployment that creates the container app rather than by a step that
  could be reordered, skipped, or copied into a second workflow without it.

## Two operational hazards

1. **ARM deployments are incremental and never delete.** Removing a resource from a template does not
   remove it from a subscription that already has it. The live `AllowAllAzureIps` rule had to be
   deleted once by hand; had that not happened, every subsequent deployment would have reported
   success — correctly, by ARM's own contract — while the rule this record exists to remove stayed on
   the server. The verification is that
   `az postgres flexible-server firewall-rule list` returns an empty list, and it lives in
   `DEPLOYMENT.md`'s end-to-end checks rather than in anyone's memory.
2. **A health check is not proof.** `/health` does not touch the database, so it returns 200 whether
   or not the private path resolves. With no firewall rule standing, the only thing that demonstrates
   the private path works is a request that actually reads or writes data. If such a request
   succeeds, the traffic went private — because the public path would have refused it.

## Alternatives considered

- **A NAT gateway giving the environment a static outbound address, plus one permanent
  single-address firewall rule.** Rejected on cost and on shape. It runs roughly four times the
  private endpoint's monthly cost, and it leaves the API's traffic on the public endpoint, so the
  server keeps answering the internet — merely to fewer callers. The private endpoint removes the
  question instead of narrowing it.
- **Narrow the rule to the environment's current outbound address and change nothing else.**
  Rejected: Microsoft does not guarantee that address is stable for a Consumption workload profile
  with no virtual network. It is the cheapest option and it is cheap because it borrows against an
  undocumented invariant. The repayment is a production outage with no deploy, no code change and no
  alert pointing at the cause.
- **Disable public network access entirely.** Rejected: the pipeline could no longer reach the
  server from a hosted runner, which takes migrations, grants, policies and RLS coverage
  verification with it and contradicts ADR 0006's mechanism. The remaining public surface is a
  server that accepts a connection from exactly one address for the length of one deploy, and only
  from a caller holding an Entra administrator token.
- **Accept the rule and document it.** Rejected, and it is the honest cheap option that deserves to
  be argued with rather than dismissed. Under Entra-only authentication ([ADR
  0007](0007-authenticate-to-postgres-with-managed-identity.md)) the rule grants reachability rather
  than access; nothing was ever exposed by it. It is rejected on two grounds. Reachability is the
  layer through which an unknown future flaw in the PostgreSQL wire protocol or in Azure's gateway
  would be exploited, and a layer that costs a subnet and an endpoint to remove is not worth keeping
  for the sake of an argument that only holds while every other layer does. And leaving it would
  mean choosing to keep ADR 0006's stated posture and the deployment's real posture different, on
  purpose, which is a worse precedent than any single firewall rule.
- **Generate the infrastructure by hand with `azd infra generate` and edit the Bicep.** Rejected: it
  moves all infrastructure out of C# and into checked-in Bicep permanently — every future resource,
  every future parameter — in order to avoid one environment recreation. The recreation is a
  one-afternoon cost with a documented checklist; hand-maintained templates are a cost on every
  change after it.

## Consequences

- **Aspire ships the annotation types for this and no public method that attaches them.**
  `DelegatedSubnetAnnotation` and `PrivateEndpointTargetAnnotation` exist in 13.4.6, so the
  delegated subnet is attached with `WithAnnotation` directly and the rest of the topology is
  written against Azure.Provisioning. Both surfaces carry evaluation-only diagnostics —
  `ASPIREAZURE003` and `AZPROVISION001` — which `AppHost.csproj` suppresses with the reasoning
  written beside the suppression. An Aspire upgrade that renames or removes either one breaks the
  build. That is the intended alarm, and the suppression must never be widened to a blanket one that
  would silence it.
- **The generated network is invisible to run mode, and stays that way.** Everything above lives
  inside the publish branch of `AppHost/Program.cs`; local development still runs PostgreSQL as a
  container reachable only from the machine it runs on. There is no environment branch in
  application code, because the private endpoint changes DNS resolution and nothing the application
  can observe.
- **Subnet ids are composed from the virtual network's id rather than read back off the subnet
  resources.** The subnets are declared inline within the network, so they have no independent
  resource to reference, and the three module outputs are interpolated Bicep expressions. A future
  change that promotes the subnets to standalone resources would have to keep the output names
  stable, because the database module and the environment annotation both consume them by name.
- **Recreating the Container Apps environment is now a documented procedure rather than an
  accident.** Any change that alters the environment's identity — a rename, a region move, a second
  network — brings a new API hostname with it, and therefore an `app-config.json` edit and a Google
  OAuth client update. The blast radius is small and entirely outside the backend, which is the only
  reason the cost is acceptable at all.
- **The private path has no test that runs before production.** There is no container-based or
  integration-level equivalent of "does DNS inside this virtual network return a private address",
  so the first deploy is the first execution. The mitigation is the second hazard above: exercise an
  endpoint that reads data, not `/health`, and treat a successful read as the proof.
