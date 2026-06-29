namespace Application.Currencies;

public sealed record CurrencyDto(string Code, string Name, string Symbol, int MinorUnit);

public sealed record CurrencyListResponse(IReadOnlyList<CurrencyDto> Items);
