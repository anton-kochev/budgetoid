# Dependency Direction Invariant

> Read this before adding a project, a `ProjectReference`, a `PackageReference`, a
> `FrameworkReference`, or an `Sdk` attribute under `BudgetoidApp/`.

**A compile-time dependency points inward and never outward: Domain declares nothing, Application
declares Domain, Infrastructure declares Application, Api declares all three.** Between rings,
MSBuild's cycle detection already refuses the project reference that would break this. What nothing
enforced until now is everything else — the package, framework and SDK edges that point outward
without closing a loop — and that held only because everyone who touched a project file happened to
agree with it.

## Two things are called a layer here

[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) uses *layer* for an
**enforcement tier** — database, then application, then client — and answers *how far down does this
rule get pushed*. This document uses **ring** for a **compile-time dependency ring** — Domain,
Application, Infrastructure, Api — and answers *what is this assembly allowed to know about*. The
two never collide, because everything ADR 0002 calls "the application" is all four rings at once: a
rule pushed down to PostgreSQL has left the rings entirely, and a rule that moves from Api to Domain
has not changed enforcement tier at all.

## The rings

| Ring | Project | May declare | May not |
| --- | --- | --- | --- |
| Innermost | `Domain` | nothing at all | every package, every project, every framework |
| Use cases | `Application` | `Domain`, and packages that carry no I/O | EF Core, Npgsql, ASP.NET Core |
| Adapters | `Infrastructure` | `Application`, EF Core, Npgsql | `Api` |
| Composition | `Api` | `Application`, `Infrastructure`, `ServiceDefaults` | — |

`AppHost`, `DbProvision`, `ServiceDefaults` and the three test projects sit outside the rings; the
graph pins them anyway, because a project the guard does not know about is a project it cannot
notice growing an edge.

## What MSBuild already refuses, and what it does not

A `ProjectReference` pointing *outward between rings* is already impossible, and not because anyone
here enforces it: since the inward reference exists, the outward one closes a loop and MSBuild fails
the restore with `MSB4006: There is a circular dependency in the target dependency graph`. Adding
`Application` to `Domain.csproj` never reaches a test. The same holds for `Application → Infrastructure`
and `Application → Api`.

Say plainly, then, what the pinned graph is actually for — it is the edges that form **no** cycle
and that nothing else notices:

- **A package.** `Application` taking a direct `Microsoft.EntityFrameworkCore` reference is the
  real-world way this architecture breaks, and MSBuild is perfectly happy with it. This is the
  headline case.
- **A `FrameworkReference`.** `Microsoft.AspNetCore.App` on `Domain` closes no loop either.
- **An `Sdk` attribute.** Flipping `Application` to `Microsoft.NET.Sdk.Web` adds no item at all.
- **A test project's reach.** Nothing references `UnitTests`, so `UnitTests → Api` is not a cycle and
  only the pin refuses it.
- **A whole new project**, which arrives with no rule attached to it by construction.

Claiming the guard stops `Domain` from referencing `Application` would be taking credit for the
compiler's work, and would leave the reader thinking the interesting cases are covered by the same
mechanism. They are not.

## What holds the line

### The graph is pinned, not described

`ProjectReferenceGraphTests` reads every `*.csproj` under the directory holding `BudgetoidApp.sln`
and renders each declaration as one row — `"<Project>: <kind> <id>"` — against a written-down set of
fifty-nine. The subject is **discovered** and the allowance is **written down**, never the reverse:
a new project fails the test by existing. That asymmetry is the same one `RlsCoverageTests` argues,
and for the same reason — a filter over the subject is how a guard silently stops covering things.

**Four kinds of edge, not two.** `ProjectReference` and `PackageReference` do not close the graph:

- `ServiceDefaults.csproj` carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. That
  one line pulls the entire ASP.NET Core shared framework with **no package to notice**. Copied onto
  `Application` or `Domain` it would hand them `HttpContext` and hosting while a pin reading only
  the two item types stayed green.
- The root `Sdk` attribute is an edge too. `Api.csproj` is `Microsoft.NET.Sdk.Web`, which *implies*
  that same framework reference and adds no item anywhere. Changing a project's `Sdk` is a
  one-attribute route to the same outcome.

So both are pinned, and every project emits an `sdk` row even when it declares nothing else — which
is what makes *Domain depends on nothing* a single line that must stay alone, rather than an absence
no assertion covers.

**Package versions are dropped; ids are kept.** A version bump moves no boundary. Pinning versions
would redden the test on every dependency-update PR, and that reflex — update the array, re-run,
move on — is precisely what would wave a genuinely new package id through in the same edit. The
`Microsoft.OpenApi` pin in `Api.csproj` is a security floor whose version *does* matter; it is not
this test's job, and `dotnet list package --vulnerable` plus the comment on that line are where it
lives.

### Api composes Infrastructure; it does not consume it

**The pinned graph provably cannot express this one.** `Api → Infrastructure` is a legitimate
reference — `Program.cs` has to call `AddInfrastructure()`, register `SessionContextInterceptor` and
configure the `DbContext` — so no arrangement of csproj rows distinguishes composing Infrastructure
from consuming it. That is why there is a second guard rather than a wider first one.

`CompositionBoundaryTests` builds the application, walks its route table, and asserts that no route
delegate declares a parameter whose type is a persistence port. What is lost when one does: the
handler's validation, its budget scoping, and — on the passkey routes — the ordering the assertion
path depends on, where the identity is published only after the signature verifies and the
transaction opens only after that. None of that lives in the repository being injected.

Two things keep it honest. The scope is **route delegate parameters**, never the Api assembly at
large: `Api/Infrastructure/HttpContextUserContext.cs` legitimately implements an Application port,
and a rule that fires on correct code earns an exemption list and then means nothing. And the
forbidden set is **derived** — every Domain interface ending `Repository`, every Application
interface ending `ReadService`, plus the three ports whose names follow no pattern — so a repository
written next month is covered the day it is written.

### A tenancy key is fixed at construction

Every `UserId` and `BudgetId` the Domain declares is written once and by nothing afterwards.
`OwnershipKeyImmutabilityTests` derives the ten of them from the assembly, pins that set, and refuses
any setter reachable from outside the declaring type — `public`, `protected`, `internal` and `init`
alike, the last because `with { UserId = someoneElse }` produces an ordinary object filed under
another user and looks immutable while doing it.

`credentials.user_id` carries this twice over. The table is exempt from row-level security — it is
read before the request has an identity a policy could key on — so nothing beneath the application
re-checks the owner, and
[ADR 0014](../decisions/0014-scope-the-credential-delete-in-the-application.md) scopes the credential
delete by the loaded entity for that reason. A settable `Credential.UserId` makes that delete
forgeable with no policy underneath to notice. See also
[ADR 0011](../decisions/0011-police-the-user-owned-tables.md) and
[data isolation](data-isolation.md).

Two of the ten are also covered by `DomainImmutabilityTests` and `TransactionTests`. **That overlap
is deliberate and must not be de-duplicated**: those tests hold a business rule about what a budget
and an account are, this one holds an isolation invariant, and collapsing them would leave whichever
reason survives carrying both weights. The arrangement is the same as the RLS policy and the EF
query filter sitting over the same row.

## What these tests cannot see

- **Declaration, not usage.** The graph says what a project *may* reference, never what it *does*. A
  referenced-but-unused package passes. That is the deliberate trade:
  `Assembly.GetReferencedAssemblies()` would check usage instead, because Roslyn omits an assembly
  reference the compiler saw no use for — and it would need the test project to reference every ring,
  which `UnitTests` refuses for `Api` on purpose.
- **Transitive packages.** A direct id is pinned; what it drags in is not.
  `Directory.Build.props` sets `ManagePackageVersionsCentrally=false`, so there is no central
  version list or lock file to lean on either.
- **Business logic in an endpoint.** The composition guard reads parameter *types*; an endpoint that
  takes the right handler and then does the wrong thing in its body is invisible to it. "No business
  logic in Api" has no reflective signature, and the one crisp part of it — which routes may mint an
  account — is already held by `UserProvisioningRouteTests`.
- **Whether every handler is actually registered.** A handler nobody wired into
  `Application/DependencyInjection.cs` is a 500 on first request, not a red test. That list is
  hand-maintained and nothing checks it; it is a known gap, deliberately left rather than missed.
- **A method that reassigns a tenancy key.** The key guard reads properties; a `Reassign(Guid)`
  method beside one is invisible to it.
- **A tenancy key under another name.** Rename `UserId` and the mutability check finds nothing to
  check — which is why the *set* is pinned as well, and why widening the name list would only move
  the blind spot rather than close it.
- **`ClientApp/`.** The scan root is the directory holding `BudgetoidApp.sln`.

Tests that lock this: `tests/UnitTests/ProjectReferenceGraphTests.cs` — adding a `PackageReference`,
a `ProjectReference`, a `FrameworkReference`, or a whole project must move a line in its pinned set,
and so must changing an `Sdk` attribute; `tests/UnitTests/OwnershipKeyImmutabilityTests.cs` —
the discovered set of `UserId`/`BudgetId` properties, and the absence of an externally reachable
setter on any of them; and `tests/IntegrationTests/CompositionBoundaryTests.cs` — no route delegate
takes a persistence port.

None of them is allowed to be green merely by having nothing to find. Each ships permanent controls
on synthetic or derived input — an EF Core package rendered onto `Application`, a public setter, an
`init` setter, a renamed key, a forbidden set asserted to be non-empty, a count of route delegates
actually inspected — so a detector that quietly stopped detecting fails its own tests before it
passes the real ones.
