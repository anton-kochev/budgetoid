using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Budgetoid.Dto;
using Budgetoid.Models;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Transactions.Queries.GetTransactions;

public class GetTransactionsQuery : IRequest<IEnumerable<TransactionDto>>
{
    public Guid AccountId { get; init; }
}

public sealed class GetTransactionsHandler : IRequestHandler<GetTransactionsQuery, IEnumerable<TransactionDto>>
{
    private readonly Container _container;

    public GetTransactionsHandler(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("Budgetoid", "Transactions");
    }

    public async Task<IEnumerable<TransactionDto>> Handle(
        GetTransactionsQuery request, CancellationToken cancellationToken)
    {
        FeedIterator<Transaction> transactions = _container.GetItemQueryIterator<Transaction>();
        IEnumerable<TransactionDto> result = (await transactions.ReadNextAsync(cancellationToken))
            .Where(t => t.AccountId == request.AccountId.ToString())
            .Select(t => new TransactionDto
            {
                Id = Guid.Parse(t.Id),
                AccountId = Guid.Parse(t.AccountId),
                Amount = t.Amount,
                Comment = t.Comment,
                Date = t.Date.ToDateOnly(),
                PayeeId = t.PayeeId,
                Tags = t.Tags
            });

        return result;
    }
}
