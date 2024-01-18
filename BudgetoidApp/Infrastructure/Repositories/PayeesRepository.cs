using Application.Abstractions;
using Domain.Entities;
using Microsoft.Azure.Cosmos;

namespace Infrastructure.Repositories;

public sealed class PayeesRepository(CosmosClient client)
    : Repository<Payee>(client), IPayeesRepository
{
    protected override string ContainerName()
    {
        return "Payees";
    }
}
