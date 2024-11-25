using System.Net;
using Application.Abstractions;
using Microsoft.Azure.Cosmos;
using User = Domain.Entities.User;

namespace Infrastructure.Repositories;

public sealed class UsersRepository(CosmosClient client)
    : Repository<User>(client), IUsersRepository
{
    public async Task<Guid> GetUserIdAsync(string email, CancellationToken cancellationToken)
    {
        User u;
        try
        {
            u = await GetContainer().ReadItemAsync<User>(
                email,
                new PartitionKey(email),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return Guid.Empty;
        }

        return u.Id;
    }

    public async Task<User> CreateUserAsync(string email, CancellationToken cancellationToken)
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = email
        };

        return await GetContainer().CreateItemAsync(user, cancellationToken: cancellationToken);
    }

    protected override string ContainerName()
    {
        return "Users";
    }
}
