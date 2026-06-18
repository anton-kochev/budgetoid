using Application.Abstractions;
using Domain.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

public sealed class BudgetoidDbContext(
    DbContextOptions<BudgetoidDbContext> options,
    IUserContext userContext) : DbContext(options)
{
    // Captured per-instance in the constructor (not referenced as userContext.UserId inside
    // the filter) so the current user is never baked into EF's cached model. This is the
    // documented multi-tenancy pattern and stays correct even if pooling is ever re-enabled.
    private readonly Guid _userId = userContext.UserId;

    public DbSet<Transaction> Transactions => Set<Transaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BudgetoidDbContext).Assembly);

        // Read-side isolation: every query against Transactions is scoped to the current user.
        // See TECH_DEBT.md "Data isolation invariant" for the escape hatches this does NOT cover.
        modelBuilder.Entity<Transaction>()
            .HasQueryFilter("UserIsolation", transaction => transaction.UserId == _userId);
    }
}
