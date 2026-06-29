using Application.Abstractions;

namespace Application.Currencies.GetCurrencies;

public sealed class GetCurrenciesHandler(ICurrencyReadService readService)
    : IQueryHandler<GetCurrenciesQuery, CurrencyListResponse>
{
    public async Task<CurrencyListResponse> HandleAsync(
        GetCurrenciesQuery query,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CurrencyDto> currencies = await readService.GetAllAsync(cancellationToken);
        return new CurrencyListResponse(currencies);
    }
}
