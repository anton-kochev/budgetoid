using Application.Abstractions;
using Domain.Accounts;
using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Currencies;
using Domain.Payees;
using Domain.Transactions;
using Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

public sealed class BudgetoidDbContext(
    DbContextOptions<BudgetoidDbContext> options,
    IBudgetContext? budgetContext = null) : DbContext(options)
{
    // Budget deliberately has no global query filter: the provisioning lookup runs before a budget
    // id exists, so every query over this set must scope by owner explicitly.
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<Payee> Payees => Set<Payee>();
    public DbSet<CategoryGroup> CategoryGroups => Set<CategoryGroup>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<User> Users => Set<User>();

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
