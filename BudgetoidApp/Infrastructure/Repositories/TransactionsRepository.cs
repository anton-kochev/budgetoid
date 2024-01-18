using System.Net;
using Application.Abstractions;
using Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Infrastructure.Repositories;

public sealed class TransactionsRepository(CosmosClient client) :
    Repository<Transaction>(client), ITransactionsRepository
{
    public async Task<Transaction?> GetAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        Transaction t;
        try
        {
            t = await GetContainer().ReadItemAsync<Transaction>(
                id.ToString(),
                new PartitionKey(userId.ToString()),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return t;
    }

    public async Task<IEnumerable<Transaction>> GetUserTransactionsAsync(Guid userId,
        CancellationToken cancellationToken)
    {
        return (await GetItemsAsync(cancellationToken))
            .Where(t => t.UserId == userId.ToString());
    }

    public async Task<IEnumerable<Transaction>> GetAccountTransactionsAsync(Guid userId, Guid accountId,
        CancellationToken cancellationToken)
    {
        return (await GetItemsAsync(cancellationToken))
            .Where(t => t.UserId == userId.ToString())
            .Where(t => t.AccountId == accountId);
    }

    public async Task<Unit> CreateAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        await GetContainer().CreateItemAsync(transaction, new PartitionKey(transaction.UserId),
            cancellationToken: cancellationToken);

        return Unit.Value;
    }

    public async Task<Unit> DeleteAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        await GetContainer().DeleteItemAsync<Transaction>(id.ToString(), new PartitionKey(userId.ToString()),
            cancellationToken: cancellationToken);

        return Unit.Value;
    }

    public async Task<Unit> UpdateAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        await GetContainer()
            .UpsertItemAsync(transaction, new PartitionKey(transaction.UserId), cancellationToken: cancellationToken);

        return Unit.Value;
    }

    protected override string ContainerName()
    {
        return "Transactions";
    }
}
