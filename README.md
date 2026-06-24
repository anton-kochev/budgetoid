# Budgetoid

[![CI](https://github.com/anton-kochev/budgetoid/actions/workflows/ci.yml/badge.svg)](https://github.com/anton-kochev/budgetoid/actions/workflows/ci.yml)

Personal budget app: .NET 10 backend (Clean Architecture + CQRS, .NET Aspire) and Angular 18 frontend.

## Prerequisites

.NET 10 SDK · .NET Aspire workload + Docker/Podman · Node.js 20+

## Run

```sh
# Backend — Aspire starts the API + PostgreSQL
cd BudgetoidApp && aspire run

# Frontend — http://localhost:4200
cd ClientApp/angular-budgetoid && npm install && npm start
```

## Test

```sh
cd BudgetoidApp && dotnet test
```

## Development workflow

Frontend commits use Lefthook to run linting and formatting on staged files before commit. Hooks are installed automatically when frontend dependencies are installed:

```sh
cd ClientApp/angular-budgetoid
npm install
```

If dependencies are already installed, install or refresh hooks manually:

```sh
cd ClientApp/angular-budgetoid
npm run prepare
```

See [CLAUDE.md](CLAUDE.md) for architecture and deploy notes, [TECH_DEBT.md](TECH_DEBT.md) for known debt.

## License

No license file present — add a [LICENSE](LICENSE) to declare usage terms.
