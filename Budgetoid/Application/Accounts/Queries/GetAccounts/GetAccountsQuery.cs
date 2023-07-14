using Budgetoid.Application.Accounts.Queries.GetAccount;
using Budgetoid.Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Application.Accounts.Queries.GetAccounts;

public record GetAccountsQuery : IRequest<IEnumerable<AccountDto>>
{
    public Guid UserId { get; init; }
}

public sealed class GetAccountsHandler : IRequestHandler<GetAccountsQuery, IEnumerable<AccountDto>>
{
    private readonly Container _accounts;
    private readonly Container _transactions;

    public GetAccountsHandler(CosmosClient cosmosClient)
    {
        _accounts = cosmosClient.GetContainer("Budgetoid", "Accounts");
        _transactions = cosmosClient.GetContainer("Budgetoid", "Transactions");
    }

    public async Task<IEnumerable<AccountDto>> Handle(GetAccountsQuery request, CancellationToken cancellationToken)
    {
        // Read the account from DB based on the provided Id and UserId
        IEnumerable<Account> accounts =
            (await _accounts.GetItemQueryIterator<Account>().ReadNextAsync(cancellationToken)).Where(a =>
                a.UserId == request.UserId.ToString());

        // Retrieve transactions associated with the accounts
        IEnumerable<Transaction> transactions =
            (await _transactions.GetItemQueryIterator<Transaction>().ReadNextAsync(cancellationToken))
            .Where(t => accounts.Any(a => a.Id == t.AccountId));

        return accounts.Select(a =>
        {
            decimal balance = transactions.Where(t => t.AccountId == a.Id).Sum(t => t.Amount);
            return new AccountDto
            {
                Balance = balance,
                Currency = a.Currency,
                Id = Guid.Parse(a.Id),
                Name = a.Name
            };
        });
    }
}
