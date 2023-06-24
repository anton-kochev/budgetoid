using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Budgetoid.Dto;
using Budgetoid.Models;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Accounts.Queries.GetAccountQuery;

public record GetAccountQuery : IRequest<AccountDto>
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
}

public sealed class GetAccountQueryHandler : IRequestHandler<GetAccountQuery, AccountDto>
{
    private readonly Container _container;

    public GetAccountQueryHandler(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("Budgetoid", "Accounts");
    }

    public async Task<AccountDto> Handle(GetAccountQuery request, CancellationToken cancellationToken)
    {
        Account a;
        try
        {
            a = await _container.ReadItemAsync<Account>(
                request.Id.ToString(),
                new PartitionKey(request.UserId.ToString()),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return new AccountDto
        {
            Id = Guid.Parse(a.Id),
            Currency = a.Currency,
            Name = a.Name
        };
    }
}
