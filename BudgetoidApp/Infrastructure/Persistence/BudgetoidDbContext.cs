using Application.Abstractions;
using Domain.Accounts;
using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Currencies;
using Domain.Payees;
using Domain.Sessions;
using Domain.Transactions;
using Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

public sealed class BudgetoidDbContext(
    DbContextOptions<BudgetoidDbContext> options,
    IBudgetContext? budgetContext = null) : DbContext(options)
{
    // Budgets, Users, Credentials and Sessions deliberately carry no BudgetIsolation query filter:
    // they are what provisioning reads and writes before an ambient budget exists, and a credential is
    // keyed on the user it lets in rather than on a budget at all. A session's reason is its own: it
    // belongs to a person and names no budget, so there is no budget to filter it by — one person's
    // session is established before any budget is ambient and outlives whichever budget was. It is
    // isolated on user_id by the user_isolation policy in the database instead. Every query over these
    // sets must therefore scope by owner explicitly.
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<Payee> Payees => Set<Payee>();
    public DbSet<CategoryGroup> CategoryGroups => Set<CategoryGroup>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<Session> Sessions => Set<Session>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BudgetoidDbContext).Assembly);
        modelBuilder.HasCollation(
            "case_insensitive",
            locale: "und-u-ks-level2",
            provider: "icu",
            deterministic: false);

        // Every filter reads the primary-constructor parameter on purpose: Roslyn lowers it to an
        // instance field, so the lambda closes over `this` and EF re-roots the closure to the context
        // instance running the query. A captured local, a static, or a service-locator call would bake
        // the first request's budget into the cached model and leak rows across tenants.
        modelBuilder.Entity<Transaction>()
            .HasQueryFilter("BudgetIsolation", transaction => transaction.BudgetId == budgetContext!.BudgetId);
        modelBuilder.Entity<Account>()
            .HasQueryFilter("BudgetIsolation", account => account.BudgetId == budgetContext!.BudgetId);
        modelBuilder.Entity<Payee>()
            .HasQueryFilter("BudgetIsolation", payee => payee.BudgetId == budgetContext!.BudgetId);
        modelBuilder.Entity<CategoryGroup>()
            .HasQueryFilter("BudgetIsolation", categoryGroup => categoryGroup.BudgetId == budgetContext!.BudgetId);
        modelBuilder.Entity<Category>()
            .HasQueryFilter("BudgetIsolation", category => category.BudgetId == budgetContext!.BudgetId);
    }
}
