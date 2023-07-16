using System.Net;
using Budgetoid.Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Application.Accounts.Queries.GetAccount;

public record GetAccountQuery(Guid Id, Guid UserId) : IRequest<AccountDto>;

public sealed class GetAccountQueryHandler : IRequestHandler<GetAccountQuery, AccountDto>
{
    private readonly Container _accounts;
    private readonly Container _transactions;

    public GetAccountQueryHandler(CosmosClient cosmosClient)
    {
        _accounts = cosmosClient.GetContainer("Budgetoid", "Accounts");
        _transactions = cosmosClient.GetContainer("Budgetoid", "Transactions");
    }

    public async Task<AccountDto> Handle(GetAccountQuery request, CancellationToken cancellationToken)
    {
        Account a;
        try
        {
            // Read the account from DB based on the provided Id and UserId
            a = await _accounts.ReadItemAsync<Account>(
                request.Id.ToString(),
                new PartitionKey(request.UserId.ToString()),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Handle the case where the account is not found and rethrow the exception
            throw;
        }

        // Retrieve transactions associated with the account
        IEnumerable<Transaction> transactions =
            (await _transactions.GetItemQueryIterator<Transaction>().ReadNextAsync(cancellationToken))
            .Where(t => t.AccountId.ToString() == a.Id);

        // Calculate the account balance based on the sum of transaction amounts
        decimal balance = transactions.Sum(t => t.Amount);

        return new AccountDto
        {
            Balance = balance,
            Currency = a.Currency,
            Id = Guid.Parse(a.Id),
            Name = a.Name
        };
    }
}
