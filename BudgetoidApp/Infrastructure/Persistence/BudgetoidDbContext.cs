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
    IUserContext? userContext = null) : DbContext(options)
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

        modelBuilder.Entity<Transaction>()
            .HasQueryFilter("UserIsolation", transaction => transaction.UserId == userContext!.UserId);
        modelBuilder.Entity<Account>()
            .HasQueryFilter("UserIsolation", account => account.UserId == userContext!.UserId);
        modelBuilder.Entity<Payee>()
            .HasQueryFilter("UserIsolation", payee => payee.UserId == userContext!.UserId);
        modelBuilder.Entity<CategoryGroup>()
            .HasQueryFilter("UserIsolation", categoryGroup => categoryGroup.UserId == userContext!.UserId);
        modelBuilder.Entity<Category>()
            .HasQueryFilter("UserIsolation", category => category.UserId == userContext!.UserId);
    }
}
