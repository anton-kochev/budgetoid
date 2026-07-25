using System.Net;
using Domain.Budgets;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntegrationTests;

/// <summary>
/// The acceptance criterion as a user experiences it: signing in is the whole setup. A user never
/// encounters "budget" as something to create (FR-001), so provisioning is asserted through a real
/// authenticated request rather than by calling the handler.
/// </summary>
public sealed class BudgetProvisioningTests
{
    [Test]
    public async Task FirstAuthenticatedRequest_CreatesExactlyOneBudget()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-first-sign-in");

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/accounts");

        // Assert
        await using BudgetoidDbContext db = CreateDb(host.ConnectionString);
        User user = await db.Users.SingleAsync();
        Budget budget = await db.Budgets.SingleAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(budget.UserId).IsEqualTo(user.Id);
        await Assert.That(budget.Name).IsEqualTo(Budget.DefaultName);
        await Assert.That(budget.BaseCurrencyCode).IsNull();
    }

    [Test]
    public async Task RepeatedSignIns_DoNotCreateAdditionalBudgets()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-returning");
        HttpResponseMessage first = await client.GetAsync("/api/accounts");

        // Act — every authenticated request re-runs provisioning; none of them may add a budget.
        HttpResponseMessage second = await client.GetAsync("/api/accounts");
        HttpResponseMessage third = await client.GetAsync("/api/categories");

        // Assert
        await using BudgetoidDbContext db = CreateDb(host.ConnectionString);

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(third.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await db.Users.CountAsync()).IsEqualTo(1);
        await Assert.That(await db.Budgets.CountAsync()).IsEqualTo(1);
    }

    private static BudgetoidDbContext CreateDb(string connectionString) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    private static async Task<PostgresTestHost> StartApiHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
