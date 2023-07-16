using Budgetoid.Application.Transactions.Queries.GetTransaction;
using Budgetoid.Domain;
using Budgetoid.Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Application.Transactions.Queries.GetTransactions;

public record GetTransactionsQuery(Guid AccountId) : IRequest<IEnumerable<TransactionDto>>;

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
        IEnumerable<TransactionDto> result =
            (await _container.GetItemQueryIterator<Transaction>().ReadNextAsync(cancellationToken))
            .Where(t => t.AccountId == request.AccountId)
            .Select(t => new TransactionDto
            {
                Id = Guid.Parse(t.Id),
                AccountId = t.AccountId,
                Amount = t.Amount,
                Comment = t.Comment,
                Date = t.Date.ToDateOnly(),
                Payee = t.Payee,
                Tags = t.Tags
            });

        return result;
    }
}
