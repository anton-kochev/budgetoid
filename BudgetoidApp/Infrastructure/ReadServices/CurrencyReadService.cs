using Application.Currencies;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class CurrencyReadService(BudgetoidDbContext dbContext) : ICurrencyReadService
{
    public async Task<IReadOnlyList<CurrencyDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.Currencies
            .AsNoTracking()
            .OrderBy(currency => currency.Code)
            .Select(currency => new CurrencyDto(currency.Code, currency.Name, currency.Symbol, currency.MinorUnit))
            .ToListAsync(cancellationToken);
    }

    public async Task<CurrencyDto?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        string normalizedCode = code.Trim().ToUpperInvariant();
        return await dbContext.Currencies
            .AsNoTracking()
            .Where(currency => currency.Code == normalizedCode)
            .Select(currency => new CurrencyDto(currency.Code, currency.Name, currency.Symbol, currency.MinorUnit))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
