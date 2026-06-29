using Application.Accounts;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class AccountReadService(BudgetoidDbContext dbContext) : IAccountReadService
{
    public async Task<IReadOnlyList<AccountDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await (
                from account in dbContext.Accounts.AsNoTracking()
                // Inner join is safe: the accounts.currency_code FK (Restrict) guarantees a
                // matching currency row always exists, so no account is silently dropped.
                join currency in dbContext.Currencies.AsNoTracking()
                    on account.CurrencyCode equals currency.Code
                orderby account.Name
                select new AccountDto(
                    account.Id,
                    account.Name,
                    account.Type,
                    account.OpeningBalance,
                    account.CreatedAtUtc,
                    account.CurrencyCode,
                    currency.Name,
                    currency.Symbol,
                    currency.MinorUnit))
            .ToListAsync(cancellationToken);
    }
}
