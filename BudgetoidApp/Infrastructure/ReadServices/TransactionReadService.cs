using Application.Transactions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class TransactionReadService(BudgetoidDbContext dbContext) : ITransactionReadService
{
    public async Task<IReadOnlyList<TransactionDto>> GetAllWithPayeeAsync(CancellationToken cancellationToken = default)
    {
        // Transactions, Accounts and Payees are scoped by BudgetoidDbContext global query filters.
        return await (
                from transaction in dbContext.Transactions.AsNoTracking()
                join account in dbContext.Accounts.AsNoTracking()
                    on transaction.AccountId equals account.Id
                // Inner join is safe: the accounts.currency_code FK (Restrict) guarantees a
                // matching currency row always exists, so no transaction is silently dropped.
                join currency in dbContext.Currencies.AsNoTracking()
                    on account.CurrencyCode equals currency.Code
                join payee in dbContext.Payees.AsNoTracking()
                    on transaction.PayeeId equals (Guid?)payee.Id into payees
                from payee in payees.DefaultIfEmpty()
                orderby transaction.Date descending, transaction.CreatedAtUtc descending
                select new TransactionDto(
                    transaction.Id,
                    transaction.Amount,
                    transaction.Date,
                    transaction.Description ?? string.Empty,
                    transaction.CreatedAtUtc,
                    transaction.AccountId,
                    account.Name,
                    account.CurrencyCode,
                    currency.Symbol,
                    transaction.PayeeId,
                    payee == null ? null : payee.Name))
            .ToListAsync(cancellationToken);
    }
}
