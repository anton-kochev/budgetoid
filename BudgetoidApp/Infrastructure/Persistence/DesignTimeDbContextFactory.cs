using Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BudgetoidDbContext>
{
    public BudgetoidDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres")
            .Options;

        // Design-time only builds the model (for migration scaffolding); it never runs a query,
        // so the query filter's user value is irrelevant. The query filter adds no schema.
        return new BudgetoidDbContext(options, new DesignTimeUserContext());
    }

    private sealed class DesignTimeUserContext : IUserContext
    {
        public Guid UserId => Guid.Empty;
    }
}
