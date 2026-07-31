# Migration Invariant

> Read this before touching `Infrastructure/Migrations/`.

**The baseline migration is frozen.** The repo used to keep a single baseline it regenerated freely.
Production's `__EFMigrationsHistory` now references the current migration id, and the deploy pipeline
applies migrations unattended on every push to `main` — so regenerating the baseline gives it a new
id, and the next push would find nothing applied and try to re-create every table against a
populated database. The human checkpoint that used to catch this is gone by design.

Schema changes are **additive migrations** from here on. CI enforces this: the `migrations-guard`
job in `.github/workflows/ci.yml` fails when any migration file from the frozen baseline onward is
modified, deleted, or renamed — only additions pass. The model snapshot is exempt because EF
rewrites it on every `migrations add`.
