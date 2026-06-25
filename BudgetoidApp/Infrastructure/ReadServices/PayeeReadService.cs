using Application.Payees;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class PayeeReadService(BudgetoidDbContext dbContext) : IPayeeReadService
{
    public async Task<IReadOnlyList<PayeeDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.Payees
            .AsNoTracking()
            .OrderBy(payee => payee.Name)
            .Select(payee => new PayeeDto(payee.Id, payee.Name))
            .ToListAsync(cancellationToken);
    }
}
