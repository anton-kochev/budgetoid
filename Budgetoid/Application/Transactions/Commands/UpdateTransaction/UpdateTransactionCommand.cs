using System.Net;
using Budgetoid.Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Application.Transactions.Commands.UpdateTransaction;

public record UpdateTransactionCommand : IRequest
{
    public Guid Id { get; init; }
    public Guid AccountId { get; init; }
    public decimal Amount { get; init; }
    public Guid CategoryId { get; init; }
    public string Comment { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string Payee { get; init; } = string.Empty;
    public string[] Tags { get; init; } = Array.Empty<string>();
}

public sealed class UpdateTransactionHandler : IRequestHandler<UpdateTransactionCommand>
{
    private readonly Container _container;

    public UpdateTransactionHandler(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("Budgetoid", "Transactions");
    }

    public async Task Handle(UpdateTransactionCommand request, CancellationToken cancellationToken)
    {
        Transaction transaction;
        try
        {
            transaction = await _container.ReadItemAsync<Transaction>(
                request.Id.ToString(),
                new PartitionKey(request.AccountId.ToString()),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw;
        }

        Transaction updated = new()
        {
            Id = transaction.Id,
            AccountId = request.AccountId,
            Amount = request.Amount,
            CategoryId = request.CategoryId,
            Comment = request.Comment,
            Date = request.Date,
            Payee = request.Payee,
            Tags = request.Tags
        };
        await _container
            .UpsertItemAsync(updated, new PartitionKey(updated.UserId), cancellationToken: cancellationToken);
    }
}
