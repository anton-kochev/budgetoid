using Application.Abstractions;

namespace Application.Accounts.GetAccounts;

public sealed class GetAccountsHandler(IAccountReadService readService)
    : IQueryHandler<GetAccountsQuery, AccountListResponse>
{
    public async Task<AccountListResponse> HandleAsync(
        GetAccountsQuery query,
        CancellationToken cancellationToken = default)
    {
        return new AccountListResponse(await readService.GetAllAsync(cancellationToken));
    }
}
