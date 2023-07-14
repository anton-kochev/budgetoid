using Budgetoid.Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Application.Payees.Commands.AddPayee;

public record AddPayeeCommand : IRequest
{
    public string Name { get; init; } = string.Empty;
    public string GeoLocation { get; init; } = string.Empty;
}

public sealed class AddPayeeHandler : IRequestHandler<AddPayeeCommand>
{
    private readonly Container _container;

    public AddPayeeHandler(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("Budgetoid", "Payees");
    }

    public async Task Handle(AddPayeeCommand request, CancellationToken cancellationToken)
    {
        Payee payee = new()
        {
            Name = request.Name,
            GeoLocation = request.GeoLocation
        };

        await _container
            .CreateItemAsync(payee, new PartitionKey(payee.UserId), cancellationToken: cancellationToken);
    }
}
