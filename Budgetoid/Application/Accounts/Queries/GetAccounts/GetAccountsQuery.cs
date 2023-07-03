using Application.Accounts.Queries.GetAccount;
using Budgetoid.Models;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Application.Accounts.Queries.GetAccounts;

public record GetAccountsQuery : IRequest<IEnumerable<AccountDto>>
{
    public Guid UserId { get; init; }
}

public sealed class GetAccountsHandler : IRequestHandler<GetAccountsQuery, IEnumerable<AccountDto>>
{
    private readonly Container _container;

    public GetAccountsHandler(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("Budgetoid", "Accounts");
    }

    public async Task<IEnumerable<AccountDto>> Handle(GetAccountsQuery request, CancellationToken cancellationToken)
    {
        FeedIterator<Account> accounts = _container.GetItemQueryIterator<Account>();
        IEnumerable<AccountDto> result = (await accounts.ReadNextAsync(cancellationToken))
            .Where(a => a.UserId == request.UserId.ToString())
            .Select(a => new AccountDto
            {
                Id = Guid.Parse(a.Id),
                Currency = a.Currency,
                Name = a.Name
            });

        return result;
    }
}
