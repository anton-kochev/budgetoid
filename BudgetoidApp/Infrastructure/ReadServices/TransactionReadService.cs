using Application.Transactions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class TransactionReadService(BudgetoidDbContext dbContext) : ITransactionReadService
{
    public async Task<IReadOnlyList<TransactionDto>> GetAllWithPayeeAsync(CancellationToken cancellationToken = default)
    {
        // Both Transactions and Payees are scoped by BudgetoidDbContext global query filters.
        return await (
                from transaction in dbContext.Transactions.AsNoTracking()
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
                    transaction.PayeeId,
                    payee == null ? null : payee.Name))
            .ToListAsync(cancellationToken);
    }
}
