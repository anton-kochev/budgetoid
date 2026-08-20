using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntegrationTests;

/// <summary>
/// Creating a transaction with a payee name writes two rows: the payee and the transaction. They
/// belong to one logical operation, so either both land or neither does. These tests drive the real
/// HTTP pipeline rather than the handler directly, so they stay independent of the handler's
/// constructor and exercise the request-scoped database context, connection and execution strategy
/// that any atomicity guarantee has to be built on.
/// </summary>
public sealed class TransactionAtomicityTests
{
    [Test]
    public async Task PostTransaction_WhenTheTransactionInsertFails_LeavesNoPayeeBehind()
    {
        // Arrange — the transaction repository is the last collaborator the handler touches, so
        // failing it lets everything before it, including the payee write, run for real first.
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        await using ApiFactory factory = host.CreateFactory(configureServices: services =>
            services.Replace(ServiceDescriptor
                .Scoped<ITransactionRepository, FailingTransactionRepository>()));
        (HttpClient client, _, _) = await factory.CreateSignedInClientAsync();
        Guid accountId = await CreateAccountAsync(client);

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-24",
            accountId,
            description = "Coffee",
            payeeName = "Starbucks",
        });
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert — the exact status is deliberately unasserted. The stub's exception type is a
        // detail of this test, and pinning the status would make the test about error mapping.
        await Assert.That(response.IsSuccessStatusCode).IsFalse();

        // The whole list, not the absence of "Starbucks": a payee stranded under any name is the
        // failure this catches, and an absence check would walk straight past one.
        await Assert.That(payees["items"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task PostTransaction_WhenItSucceeds_CommitsBothTheTransactionAndThePayee()
    {
        // Arrange — no stub here; this is the ordinary path through the real repository.
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();
        Guid accountId = await CreateAccountAsync(client);

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-24",
            accountId,
            description = "Coffee",
            payeeName = "Starbucks",
        });
        JsonNode transactions = await GetJsonAsync(client, "/api/transactions");
        JsonNode payees = await GetJsonAsync(client, "/api/payees");

        // Assert — keep this test. It looks redundant next to the creation tests elsewhere, but it
        // is the counterweight to the test above: without it, never committing the payee at all
        // would satisfy the atomicity test and nothing in the suite would object.
        await Assert.That(response.IsSuccessStatusCode).IsTrue();
        await Assert.That(transactions["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payees["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(payees["items"]!.AsArray()[0]!["name"]!.GetValue<string>())
            .IsEqualTo("Starbucks");
    }

    // Fails on the transaction write and only on the transaction write. The other members exist to
    // satisfy the interface; nothing in these tests reaches them.
    private sealed class FailingTransactionRepository : ITransactionRepository
    {
        public Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Simulated failure after the payee was written and before the transaction was.");

        public Task<Transaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Transaction?>(null);

        // Nothing to store — the real repository saves an already-tracked entity the caller mutated
        // in place, so a stub has nothing left to do. Deliberately does not throw: no test here
        // edits a transaction, and failing this would claim a rollback path these tests never cover.
        public Task UpdateAsync(Transaction transaction, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(Transaction transaction, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        // Also deliberately does not throw: nothing here erases an account, and failing this would
        // claim a rollback path these tests never cover.
        public Task DeleteAllForAmbientBudgetAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string path) =>
        (await JsonNode.ParseAsync(await client.GetStreamAsync(path)))!;
}
