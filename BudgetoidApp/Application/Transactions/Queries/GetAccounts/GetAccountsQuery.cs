using Application.Abstractions;
using Domain.Entities;
using MediatR;

namespace Application.Transactions.Queries.GetAccounts;

public record GetAccountsQuery(Guid UserId) : IRequest<IEnumerable<AccountDto>>;

public sealed class GetAccountsHandler(IAccountsRepository accountsRepository)
    : IRequestHandler<GetAccountsQuery, IEnumerable<AccountDto>>
{
    public async Task<IEnumerable<AccountDto>> Handle(GetAccountsQuery request, CancellationToken cancellationToken)
    {
        IEnumerable<Account> entities = await accountsRepository.GetAccountsByUserId(request.UserId, cancellationToken);

        return entities.ToDto();
    }
}
