using Budgetoid.Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Application.Transactions.Commands.DeleteTransaction;

public record DeleteTransactionCommand(Guid Id, Guid AccountId) : IRequest;

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
            new PartitionKey(request.AccountId.ToString()),
            cancellationToken: cancellationToken);
    }
}
