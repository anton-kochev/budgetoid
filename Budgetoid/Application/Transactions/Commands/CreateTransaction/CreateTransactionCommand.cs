using Budgetoid.Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Application.Transactions.Commands.CreateTransaction;

public record CreateTransactionCommand : IRequest<Unit>
{
    public Guid AccountId { get; init; } = Guid.Empty;
    public decimal Amount { get; init; }
    public Guid CategoryId { get; init; } = Guid.Empty;
    public string Comment { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string Payee { get; init; } = string.Empty;
    public string[] Tags { get; init; } = Array.Empty<string>();
    public Guid UserId { get; init; } = Guid.Empty;
}

public sealed class CreateTransactionHandler : IRequestHandler<CreateTransactionCommand, Unit>
{
    private readonly Container _transactions;

    public CreateTransactionHandler(CosmosClient cosmosClient)
    {
        _transactions = cosmosClient.GetContainer("Budgetoid", "Transactions");
    }

    public async Task<Unit> Handle(CreateTransactionCommand request, CancellationToken cancellationToken)
    {
        Transaction transaction = new()
        {
            AccountId = request.AccountId,
            Amount = request.Amount,
            CategoryId = request.CategoryId,
            Comment = request.Comment,
            Date = request.Date,
            Payee = request.Payee,
            Tags = request.Tags,
            UserId = request.UserId.ToString()
        };

        await _transactions
            .CreateItemAsync(transaction, new PartitionKey(transaction.UserId),
                cancellationToken: cancellationToken);

        return Unit.Value;
    }
}
