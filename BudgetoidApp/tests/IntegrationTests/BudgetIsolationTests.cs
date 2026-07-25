using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Accounts;
using Domain.Transactions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntegrationTests;

public sealed class BudgetIsolationTests
{
    [Test]
    public async Task QueryFilter_HidesOtherBudgetsTransactions_EvenWithoutAnExplicitWhere()
    {
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetA = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid budgetB = await host.SeedBudgetAsync("google-b", "b@example.com");

        Guid transactionId;
        await using (BudgetoidDbContext dbA = CreateDb(host, budgetA))
        {
            Account account = Account.Create(budgetA, "Checking", AccountType.Checking, 0m, "USD", DateTime.UtcNow);
            dbA.Accounts.Add(account);
            await dbA.SaveChangesAsync();

            Transaction transaction = Transaction.Create(
                budgetA,
                account.Id,
                -10m,
                new DateOnly(2026, 6, 12),
                "Budget A groceries",
                new DateTime(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc));
            transactionId = transaction.Id;
            dbA.Transactions.Add(transaction);
            await dbA.SaveChangesAsync();
        }

        // Budget B, in the SAME process: even an unfiltered query must see nothing of budget A's.
        // Running A-then-B in one process also guards against the budget being baked into EF's
        // cached model (a captured-service-reference filter would leak A's row to B here). Never
        // split this across two processes — that is exactly the regression it exists to catch.
        await using (BudgetoidDbContext dbB = CreateDb(host, budgetB))
        {
            List<Transaction> all = await dbB.Transactions.ToListAsync();
            await Assert.That(all.Count).IsEqualTo(0);

            Transaction? byId = await dbB.Transactions.FirstOrDefaultAsync(t => t.Id == transactionId);
            await Assert.That(byId).IsNull();
        }

        // Budget A still sees its own row (after budget B queried in the same process).
        await using (BudgetoidDbContext dbA = CreateDb(host, budgetA))
        {
            List<Transaction> mine = await dbA.Transactions.ToListAsync();
            await Assert.That(mine.Count).IsEqualTo(1);
            await Assert.That(mine[0].Id).IsEqualTo(transactionId);
        }
    }

    [Test]
    public async Task QueryFilter_HidesAnotherBudgetOfTheSameOwner()
    {
        // Two budgets under ONE user: proves the filter closes over the budget and not the owner.
        // A surviving user-scoped predicate would still pass the two-user test above and fail here.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-a", "a@example.com");
        Guid budgetA = await host.SeedAdditionalBudgetAsync(userId, "Household");
        Guid budgetB = await host.SeedAdditionalBudgetAsync(userId, "Side Project");

        await using (BudgetoidDbContext dbA = CreateDb(host, budgetA))
        {
            dbA.Accounts.Add(Account.Create(
                budgetA,
                "Checking",
                AccountType.Checking,
                0m,
                "USD",
                DateTime.UtcNow));
            await dbA.SaveChangesAsync();
        }

        await using BudgetoidDbContext dbB = CreateDb(host, budgetB);
        List<Account> visibleToB = await dbB.Accounts.ToListAsync();

        await Assert.That(visibleToB.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetTransactions_DoesNotReturnAnotherBudgetsTransactions()
    {
        await using PostgresTestHost host = new();
        await host.StartAsync();
        const string userA = "google-a";
        const string userB = "google-b";

        // Two factories over the SAME database container — one per user, so one per default budget.
        await using ApiFactory factoryA = host.CreateFactory(userA);
        await using ApiFactory factoryB = host.CreateFactory(userB);

        HttpClient clientA = factoryA.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(clientA);
        HttpResponseMessage created = await clientA.PostAsJsonAsync("/api/transactions", new
        {
            amount = -42.50m,
            date = "2026-06-12",
            accountId,
            description = "Budget A lunch",
        });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Budget B must not see budget A's transaction.
        HttpClient clientB = factoryB.CreateAuthenticatedClient();
        HttpResponseMessage listB = await clientB.GetAsync("/api/transactions");
        await Assert.That(listB.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonNode? jsonB = await JsonNode.ParseAsync(await listB.Content.ReadAsStreamAsync());
        await Assert.That(jsonB!["items"]!.AsArray().Count).IsEqualTo(0);

        // Budget A still sees its own transaction.
        HttpResponseMessage listA = await clientA.GetAsync("/api/transactions");
        JsonNode? jsonA = await JsonNode.ParseAsync(await listA.Content.ReadAsStreamAsync());
        await Assert.That(jsonA!["items"]!.AsArray().Count).IsEqualTo(1);
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            name = "Checking",
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    private static BudgetoidDbContext CreateDb(RepositoryTestHost host, Guid budgetId)
    {
        DbContextOptions<BudgetoidDbContext> options = new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options;

        return new BudgetoidDbContext(options, new TestBudgetContext(budgetId));
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
