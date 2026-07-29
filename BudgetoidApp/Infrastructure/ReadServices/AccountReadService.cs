using Application.Accounts;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

// Both queries below inner-join Currencies: the accounts.currency_code FK (Restrict) guarantees a
// matching currency row always exists, so the join can neither drop an account from the list nor
// turn an account that exists into a 404.
public sealed class AccountReadService(BudgetoidDbContext dbContext) : IAccountReadService
{
    public async Task<IReadOnlyList<AccountDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await (
                from account in dbContext.Accounts.AsNoTracking()
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

    public async Task<AccountDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await (
                from account in dbContext.Accounts.AsNoTracking()
                join currency in dbContext.Currencies.AsNoTracking()
                    on account.CurrencyCode equals currency.Code
                where account.Id == id
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
            .FirstOrDefaultAsync(cancellationToken);
    }
}
