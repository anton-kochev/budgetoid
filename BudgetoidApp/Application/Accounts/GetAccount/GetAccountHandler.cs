using Application.Abstractions;

namespace Application.Accounts.GetAccount;

public sealed class GetAccountHandler(IAccountReadService readService)
    : IQueryHandler<GetAccountQuery, AccountDto?>
{
    public async Task<AccountDto?> HandleAsync(
        GetAccountQuery query,
        CancellationToken cancellationToken = default)
    {
        return await readService.GetByIdAsync(query.Id, cancellationToken);
    }
}
