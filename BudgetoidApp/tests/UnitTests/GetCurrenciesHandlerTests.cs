using Application.Currencies;
using Application.Currencies.GetCurrencies;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class GetCurrenciesHandlerTests
{
    [Test]
    public async Task HandleAsync_ReturnsCurrenciesOrderedByCode()
    {
        var service = new InMemoryCurrencyReadService();
        service.Add(new CurrencyDto("USD", "US Dollar", "$", 2));
        service.Add(new CurrencyDto("JPY", "Yen", "¥", 0));
        var handler = new GetCurrenciesHandler(service);

        CurrencyListResponse response = await handler.HandleAsync(new GetCurrenciesQuery());

        await Assert.That(response.Items.Select(currency => currency.Code)).IsEquivalentTo(["JPY", "USD"]);
    }
}
