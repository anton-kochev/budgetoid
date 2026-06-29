using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Currencies;

namespace IntegrationTests;

public sealed class CurrencyIntegrationTests
{
    [Test]
    public async Task GetCurrencies_ReturnsSeededList()
    {
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();

        JsonNode json = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/currencies")))!;
        JsonArray items = json["items"]!.AsArray();

        await Assert.That(items.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(items.Any(item => item!["code"]!.GetValue<string>() == "USD")).IsTrue();
        await Assert.That(items.Any(item => item!["code"]!.GetValue<string>() == "JPY" && item!["minorUnit"]!.GetValue<int>() == 0)).IsTrue();
        await Assert.That(items.Select(item => item!["code"]!.GetValue<string>()).ToArray()).IsEquivalentTo(
            items.Select(item => item!["code"]!.GetValue<string>()).Order().ToArray());
    }

    [Test]
    public async Task GetCurrencies_RequiresAuth()
    {
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/currencies");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task SeededCurrencies_AllSatisfyInvariants()
    {
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();

        JsonNode json = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/currencies")))!;

        foreach (JsonNode? item in json["items"]!.AsArray())
        {
            _ = Currency.Create(
                item!["code"]!.GetValue<string>(),
                item["name"]!.GetValue<string>(),
                item["symbol"]!.GetValue<string>(),
                item["minorUnit"]!.GetValue<int>());
        }
    }

    [Test]
    public async Task CreateAccount_WithUnknownCurrency_IsRejected()
    {
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            name = "Checking",
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "ZZZ",
        });
        JsonNode? problem = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!["CurrencyCode"] is not null).IsTrue();
    }

    private static async Task<PostgresTestHost> StartApiHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
