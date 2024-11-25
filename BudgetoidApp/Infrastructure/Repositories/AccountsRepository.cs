using System.Net;
using Application.Abstractions;
using Domain.Entities;
using Microsoft.Azure.Cosmos;

namespace Infrastructure.Repositories;

public sealed class AccountsRepository(CosmosClient client)
    : Repository<Account>(client), IAccountsRepository
{
    public async Task<IEnumerable<Account>> GetAccountsByUserId(Guid userId, CancellationToken cancellationToken)
    {
        return (await GetItemsAsync(cancellationToken))
            .Where(t => t.UserId == userId.ToString());
    }

    public async Task<Account?> GetAccountById(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        Account a;
        try
        {
            a = await GetContainer().ReadItemAsync<Account>(
                id.ToString(),
                new PartitionKey(userId.ToString()),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return a;
    }

    protected override string ContainerName()
    {
        return "Accounts";
    }
}
