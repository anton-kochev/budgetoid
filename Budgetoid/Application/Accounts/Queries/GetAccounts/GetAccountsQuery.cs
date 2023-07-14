using Budgetoid.Application.Accounts.Queries.GetAccount;
using Budgetoid.Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Application.Accounts.Queries.GetAccounts;

public record GetAccountsQuery(Guid UserId) : IRequest<IEnumerable<AccountDto>>;

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
        // Retrieve all accounts associated with the provided UserId
        IEnumerable<Account> accounts = await GetAccountsAsync(request.UserId, cancellationToken);

        // Retrieve transactions associated with the accounts
        IEnumerable<Transaction> transactions = await GetTransactionsAsync(accounts, cancellationToken);

        // Calculate the balance for each account
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

    private async Task<IList<Account>> GetAccountsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return (await _accounts.GetItemQueryIterator<Account>().ReadNextAsync(cancellationToken))
            .Where(a => a.UserId == userId.ToString())
            .ToList();
    }

    private async Task<IList<Transaction>> GetTransactionsAsync(IEnumerable<Account> accounts,
        CancellationToken cancellationToken)
    {
        IEnumerable<Guid> accountIds = accounts.Select(a => Guid.Parse(a.Id));

        return (await _transactions.GetItemQueryIterator<Transaction>().ReadNextAsync(cancellationToken))
            .Where(t => accountIds.Contains(Guid.Parse(t.AccountId)))
            .ToList();
    }
}
