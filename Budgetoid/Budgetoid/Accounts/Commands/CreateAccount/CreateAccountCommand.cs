using System;
using System.Threading;
using System.Threading.Tasks;
using Budgetoid.Models;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Accounts.Commands.CreateAccount;

public record CreateAccountCommand : IRequest<Guid>
{
    public string Currency { get; init; }
    public string Name { get; init; }
    public Guid UserId { get; init; }
}

public sealed class CreateAccountCommandHandler : IRequestHandler<CreateAccountCommand, Guid>
{
    private readonly Container _container;

    public CreateAccountCommandHandler(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("Budgetoid", "Accounts");
    }

    public async Task<Guid> Handle(CreateAccountCommand request, CancellationToken cancellationToken)
    {
        Account account = new()
        {
            Currency = request.Currency,
            Name = request.Name,
            UserId = request.UserId.ToString()
        };

        await _container
            .CreateItemAsync(account, new PartitionKey(account.UserId),
                cancellationToken: cancellationToken);

        return Guid.Parse(account.Id);
    }
}
