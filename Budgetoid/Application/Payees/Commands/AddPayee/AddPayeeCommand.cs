using System.Net;
using Budgetoid.Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Application.Payees.Commands.AddPayee;

public record AddPayeeCommand : IRequest
{
    public string GeoLocation { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Guid UserId { get; init; } = Guid.Empty;
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
            GeoLocation = request.GeoLocation,
            Name = request.Name,
            UserId = request.UserId.ToString()
        };

        try
        {
            await _container
                .CreateItemAsync(payee, new PartitionKey(payee.UserId), cancellationToken: cancellationToken);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            // Console.WriteLine($"{payee.Name} already exists");
        }
    }
}
