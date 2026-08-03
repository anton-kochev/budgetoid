# Migration Invariant

> Read this before touching `Infrastructure/Persistence/Migrations/`.

**Applied migrations are frozen.** The repo used to keep a single baseline it regenerated freely.
Production's `__EFMigrationsHistory` now references the current migration id, and the deploy pipeline
applies migrations unattended on every push to `main` — so regenerating the baseline gives it a new
id, and the next push would find nothing applied and try to re-create every table against a
populated database. The human checkpoint that used to catch this is gone by design.

Schema changes are **additive migrations** from here on, and two things hold that line. The
`migrations-guard` job in `.github/workflows/ci.yml` fails when any migration file from
`FROZEN_FROM` onward is modified, deleted, or renamed — only additions pass; the model snapshot is
exempt because EF rewrites it on every `migrations add`. And `Migrations_KeepTheBaselineFrozen` in
`tests/IntegrationTests/BudgetoidDbContextConstructionTests.cs` pins the first migration id to a
literal, so a regenerated or back-dated baseline fails the suite while a tenth additive migration
passes.

## The rebaseline window

`REBASELINE_WINDOW` in that same job is **open**, and while it is open the freeze is suspended: the
baseline may be regenerated and the guard reports the change instead of failing on it. It is open
because the production database holds no data, and because the schema changes still ahead of it are
worth landing as one initial migration rather than as a chain of migrations no database ever replays
step by step.

The window makes a regenerated baseline *permitted*, not *free*. The new baseline carries a new id,
so production's history no longer matches anything the pipeline is about to apply. Whoever
regenerates the baseline resets that history in the same deploy — `DEPLOYMENT.md`, Step 3, holds
the procedure — or the deploy fails on the first `CREATE TABLE`. The window is safe only for as
long as the database it drops holds nothing anyone wants back.

The window covers the CI job and nothing else. `Migrations_KeepTheBaselineFrozen` still fails on a
regenerated baseline, by design: the literal id in that test is a checkpoint a human edits
deliberately, which is precisely what a rebaseline should be. Regenerating the baseline therefore
means editing that literal too, in the same commit.

Closing the window is two lines in `migrations-guard`: set `REBASELINE_WINDOW` to `closed` and
`FROZEN_FROM` to the new baseline's id. That belongs in the commit that lands the last schema change
the window exists for, not in a follow-up — an open window over a database that has started
collecting real data is the exact failure this document exists to prevent.
