using Application.Abstractions;
using Domain.Entities;
using Microsoft.Azure.Cosmos;

namespace Infrastructure.Repositories;

public sealed class AccountsRepository(CosmosClient client)
    : Repository<Account>(client), IAccountsRepository
{
    protected override string ContainerName()
    {
        return "Accounts";
    }
}
