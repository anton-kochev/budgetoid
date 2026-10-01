using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Currencies;
using TestSupport;

namespace IntegrationTests;

public sealed class CurrencyIntegrationTests
{
    [Test]
    public async Task GetCurrencies_ReturnsSeededList()
    {
        await using PostgresTestHost host = await StartApiHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();

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
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();

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
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            // THE ONLY THING WRONG WITH THIS BODY IS THE CURRENCY, and that is the whole design of the
            // case. The id, the sealed name and the blind index all have to be well-formed, because
            // CreateAccountHandler joins every shape failure into one errors dictionary: a flat name or a
            // missing id would still produce a 400, the status assertion below would still pass, and the
            // errors check would then be reading a document that happens to also carry CurrencyCode
            // rather than one that carries it alone. Routed through SealedNarrative so the two opaque
            // members are the values the route accepts.
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Checking"),
            nameKey = SealedNarrative.EncodedIndex("Checking"),
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "ZZZ",
        });
        JsonNode? problem = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem!["errors"]!["CurrencyCode"] is not null).IsTrue();

        // And NOTHING ELSE is reported, which is what pins the paragraph above rather than leaving it as
        // a claim. Without this line the case passes on a body whose id, name and index are all malformed,
        // and stops being a test about an unknown currency at all.
        await Assert.That(problem["errors"]!.AsObject().Count).IsEqualTo(1);
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because every
    /// authenticated request in this file arrives with a session cookie rather than a provider bearer.
    /// </summary>
    /// <remarks>
    /// <see cref="GetCurrencies_RequiresAuth" /> rides on it unchanged: its client carries nothing at
    /// all, which is refused by the fallback policy whichever scheme would have answered it.
    /// </remarks>
    private static async Task<PostgresTestHost> StartApiHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }
}
