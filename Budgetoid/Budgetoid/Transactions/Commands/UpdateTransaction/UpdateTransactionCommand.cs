using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Budgetoid.Application.Common.Exceptions;
using Budgetoid.Dto;
using Budgetoid.Models;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Transactions.Commands.UpdateTransaction;

public record UpdateTransactionCommand : IRequest
{
    public Guid Id { get; init; }
    public Guid AccountId { get; init; }
    public UpdateTransactionDto Update { get; init; }
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
            throw new NotFoundException();
        }

        Transaction updated = new()
        {
            Id = transaction.Id,
            CreatedOn = transaction.CreatedOn,

            AccountId = request.Update.AccountId.ToString(),
            Amount = request.Update.Amount,
            CategoryId = request.Update.CategoryId,
            Comment = request.Update.Comment,
            Date = request.Update.Date,
            PayeeId = request.Update.PayeeId,
            Tags = request.Update.Tags
        };
        await _container
            .UpsertItemAsync(updated, new PartitionKey(updated.AccountId), cancellationToken: cancellationToken);
    }
}
