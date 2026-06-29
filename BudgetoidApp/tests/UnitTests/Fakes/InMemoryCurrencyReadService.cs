using Application.Currencies;

namespace UnitTests.Fakes;

public sealed class InMemoryCurrencyReadService : ICurrencyReadService
{
    private readonly List<CurrencyDto> _currencies = [new("USD", "US Dollar", "$", 2)];

    public void Add(CurrencyDto currency)
    {
        _currencies.RemoveAll(existing => existing.Code == currency.Code);
        _currencies.Add(currency);
    }

    public Task<IReadOnlyList<CurrencyDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CurrencyDto> currencies = _currencies.OrderBy(currency => currency.Code).ToList();
        return Task.FromResult(currencies);
    }

    public Task<CurrencyDto?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        CurrencyDto? currency = _currencies.SingleOrDefault(currency => currency.Code == code.Trim().ToUpperInvariant());
        return Task.FromResult(currency);
    }
}
