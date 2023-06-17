using System;
using System.Threading;
using System.Threading.Tasks;
using Budgetoid.Models;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Transactions.Commands.DeleteTransaction;

public record DeleteTransactionCommand : IRequest
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
}

public sealed class DeleteTransactionCommandHandler : IRequestHandler<DeleteTransactionCommand>
{
    private readonly Container _container;

    public DeleteTransactionCommandHandler(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("Budgetoid", "Transactions");
    }

    public async Task Handle(DeleteTransactionCommand request, CancellationToken cancellationToken)
    {
        await _container.DeleteItemAsync<Transaction>(
            request.Id.ToString(),
            new PartitionKey(request.UserId.ToString()),
            cancellationToken: cancellationToken);
    }
}
