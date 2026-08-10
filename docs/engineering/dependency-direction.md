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

## What these tests cannot see

- **Declaration, not usage.** The graph says what a project *may* reference, never what it *does*. A
  referenced-but-unused package passes. That is the deliberate trade:
  `Assembly.GetReferencedAssemblies()` would check usage instead, because Roslyn omits an assembly
  reference the compiler saw no use for — and it would need the test project to reference every ring,
  which `UnitTests` refuses for `Api` on purpose.
- **Transitive packages.** A direct id is pinned; what it drags in is not.
  `Directory.Build.props` sets `ManagePackageVersionsCentrally=false`, so there is no central
  version list or lock file to lean on either.
- **What the code does with an allowed reference.** `Api → Infrastructure` is legitimate — `Program.cs`
  has to compose it — so the graph can never distinguish composing Infrastructure from consuming it.
- **`ClientApp/`.** The scan root is the directory holding `BudgetoidApp.sln`.

Tests that lock this: `tests/UnitTests/ProjectReferenceGraphTests.cs`. Adding a `PackageReference`,
a `ProjectReference`, a `FrameworkReference`, or a whole project must move a line in its pinned set;
so must changing an `Sdk` attribute. It ships with a negative control that renders an EF Core
package onto `Application` and asserts the pinned set refuses it, so the guard is never merely green
by having nothing to find.
