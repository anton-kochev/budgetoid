using System.Net;
using Budgetoid.Domain;
using Budgetoid.Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Application.Transactions.Queries.GetTransaction;

public record GetTransactionQuery(Guid Id, Guid AccountId) : IRequest<TransactionDto>;

public sealed class GetTransactionHandler : IRequestHandler<GetTransactionQuery, TransactionDto>
{
    private readonly Container _container;

    public GetTransactionHandler(CosmosClient cosmosClient)
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
                new PartitionKey(request.AccountId.ToString()),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw;
        }

        return new TransactionDto
        {
            Id = Guid.Parse(t.Id),
            AccountId = t.AccountId,
            Amount = t.Amount,
            Comment = t.Comment,
            CategoryId = t.CategoryId,
            Date = t.Date.ToDateOnly(),
            Payee = t.Payee,
            Tags = t.Tags
        };
    }
}
