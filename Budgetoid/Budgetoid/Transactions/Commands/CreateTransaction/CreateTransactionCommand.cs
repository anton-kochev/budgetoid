using System;
using System.Threading;
using System.Threading.Tasks;
using Budgetoid.Models;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Transactions.Commands.CreateTransaction;

public record CreateTransactionCommand : IRequest<Guid>
{
    public Guid AccountId { get; init; }
    public decimal Amount { get; init; }
    public Guid CategoryId { get; init; }
    public string Comment { get; init; }
    public DateTime Date { get; init; }
    public Guid PayeeId { get; init; }
    public string[] Tags { get; init; }
}

public sealed class CreateTransactionHandler : IRequestHandler<CreateTransactionCommand, Guid>
{
    private readonly Container _container;

    public CreateTransactionHandler(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("Budgetoid", "Transactions");
    }

    public async Task<Guid> Handle(CreateTransactionCommand request, CancellationToken cancellationToken)
    {
        Transaction transaction = new()
        {
            AccountId = request.AccountId.ToString(),
            Amount = request.Amount,
            CategoryId = request.CategoryId,
            Comment = request.Comment,
            CreatedOn = DateTime.UtcNow,
            Date = request.Date,
            PayeeId = request.PayeeId,
            Tags = request.Tags
        };

        await _container
            .CreateItemAsync(transaction, new PartitionKey(transaction.AccountId),
                cancellationToken: cancellationToken);

        return Guid.Parse(transaction.Id);
    }
}
