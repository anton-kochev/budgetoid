namespace Application.Currencies;

public interface ICurrencyReadService
{
    Task<IReadOnlyList<CurrencyDto>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<CurrencyDto?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
}
