using Application.Transactions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class TransactionReadService(BudgetoidDbContext dbContext) : ITransactionReadService
{
    public async Task<IReadOnlyList<TransactionDto>> GetAllWithPayeeAsync(
        CancellationToken cancellationToken = default)
    {
        return await (
                from transaction in dbContext.Transactions.AsNoTracking()
                join account in dbContext.Accounts.AsNoTracking()
                    on transaction.AccountId equals account.Id
                join currency in dbContext.Currencies.AsNoTracking()
                    on account.CurrencyCode equals currency.Code
                join payee in dbContext.Payees.AsNoTracking()
                    on transaction.PayeeId equals (Guid?)payee.Id into payees
                from payee in payees.DefaultIfEmpty()
                join category in dbContext.Categories.AsNoTracking()
                    on transaction.CategoryId equals (Guid?)category.Id into categories
                from category in categories.DefaultIfEmpty()
                join categoryGroup in dbContext.CategoryGroups.AsNoTracking()
                    on category.CategoryGroupId equals categoryGroup.Id into categoryGroups
                from categoryGroup in categoryGroups.DefaultIfEmpty()
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
                    payee == null ? null : payee.Name,
                    transaction.CategoryId,
                    category == null ? null : category.Name,
                    categoryGroup == null ? null : categoryGroup.Id,
                    categoryGroup == null ? null : categoryGroup.Name))
            .ToListAsync(cancellationToken);
    }
}
