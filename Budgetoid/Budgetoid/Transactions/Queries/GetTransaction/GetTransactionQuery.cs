using System;
using System.Threading;
using System.Threading.Tasks;
using Budgetoid.Dto;
using Budgetoid.Models;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Transactions.Queries.GetTransaction;

public record GetTransactionQuery : IRequest<TransactionDto>
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
}

public sealed class GetTransactionQueryHandler : IRequestHandler<GetTransactionQuery, TransactionDto>
{
    private readonly Container _container;
    
    public GetTransactionQueryHandler(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("Budgetoid", "Transactions");
    }

    public async Task<TransactionDto> Handle(GetTransactionQuery request, CancellationToken cancellationToken)
    {
        Transaction t;
        try
        {
            t = await _container.ReadItemAsync<Transaction>(
                request.Id.ToString(),
                new PartitionKey(request.UserId.ToString()),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        return new TransactionDto
        {
            Id = Guid.Parse(t.Id),
            AccountId = t.AccountId,
            Amount = t.Amount,
            Comment = t.Comment,
            CategoryId = t.CategoryId,
            Date = t.Date.ToDateOnly(),
            PayeeId = t.PayeeId,
            Tags = t.Tags,
        };
    }
}
