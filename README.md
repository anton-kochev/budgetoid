<p align="center">
  <img src="branding/halo/app-icon.svg" alt="Budgetoid" width="104" height="104">
</p>

<h1 align="center">Budgetoid</h1>

<p align="center">
  Personal budget app — .NET 10 backend (Clean Architecture + CQRS, .NET Aspire) &amp; Angular 21 frontend.
</p>

<p align="center">
  <a href="https://github.com/anton-kochev/budgetoid/actions/workflows/ci.yml"><img src="https://github.com/anton-kochev/budgetoid/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
</p>

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

Commits use Lefthook to run formatting and linting on staged frontend and backend files before commit. Backend formatting follows `BudgetoidApp/.editorconfig`. Hooks are installed automatically when frontend dependencies are installed:

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
