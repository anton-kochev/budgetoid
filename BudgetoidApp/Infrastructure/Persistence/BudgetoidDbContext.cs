using Application.Abstractions;
using Domain.Accounts;
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
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<Payee> Payees => Set<Payee>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BudgetoidDbContext).Assembly);

        // Nondeterministic ICU collation: case-insensitive, accent-sensitive. The payee name
        // column uses it so equality comparisons and the unique index fold case in PostgreSQL
        // itself (see PayeeConfiguration) — no lower() and no C#-side case folding to drift apart.
        modelBuilder.HasCollation("case_insensitive", locale: "und-u-ks-level2", provider: "icu", deterministic: false);

        // Read-side isolation: every query against Transactions is scoped to the current user,
        // read live from the scoped IUserContext at query time (after the auth middleware runs),
        // so no repository has to remember to stamp the user. See TECH_DEBT.md "Data isolation
        // invariant" for the escape hatches this does NOT cover.
        //
        // The provider is optional so the context still constructs without a resolved user
        // (migrations, design-time factory, seeding) — those paths never query Transactions, so
        // the userContext! dereference is never reached for a null provider. Every real query path
        // has a DI-injected (production) or test-supplied provider.
        modelBuilder.Entity<Transaction>()
            .HasQueryFilter("UserIsolation", transaction => transaction.UserId == userContext!.UserId);
        modelBuilder.Entity<Account>()
            .HasQueryFilter("UserIsolation", account => account.UserId == userContext!.UserId);
        modelBuilder.Entity<Payee>()
            .HasQueryFilter("UserIsolation", payee => payee.UserId == userContext!.UserId);

        // Currencies are global ISO-4217 reference data seeded by migrations. They deliberately
        // have no UserId and no query filter, unlike user-owned accounts, transactions and payees.

    }
}
