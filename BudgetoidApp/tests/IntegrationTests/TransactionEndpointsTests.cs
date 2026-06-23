using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace IntegrationTests;

public sealed class TransactionEndpointsTests
{
    [Test]
    public async Task PostValidTransaction_ReturnsCreatedLocationAndCamelCaseDto()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -42.50m,
            date = "2026-06-12",
            description = "Groceries"
        });
        JsonNode? json = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(response.Headers.Location?.ToString().StartsWith("/api/transactions/")).IsTrue();
        await Assert.That(json!["amount"]!.GetValue<decimal>()).IsEqualTo(-42.50m);
        await Assert.That(json["date"]!.GetValue<string>()).IsEqualTo("2026-06-12");
        await Assert.That(json["description"]!.GetValue<string>()).IsEqualTo("Groceries");
        await Assert.That(json["createdAtUtc"] is not null).IsTrue();
    }

    [Test]
    public async Task PostInvalidTransaction_ReturnsValidationProblemDetails()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpResponseMessage response = await host.Factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/transactions", new
        {
            amount = 0,
            date = "2026-06-12",
            description = ""
        });
        JsonNode? json = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        await Assert.That(json!["errors"]!["Amount"] is not null).IsTrue();
        await Assert.That(json["errors"]!["Description"] is not null).IsTrue();
    }

    [Test]
    public async Task PostMalformedJson_ReturnsBadRequest()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpResponseMessage response = await host.Factory.CreateAuthenticatedClient().PostAsync(
            "/api/transactions",
            new StringContent("{", Encoding.UTF8, "application/json"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task GetTransactions_ReturnsNewestFirst()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync("/api/transactions",
            new { amount = 1m, date = "2026-06-11", description = "Older" });
        await Task.Delay(2);
        await client.PostAsJsonAsync("/api/transactions",
            new { amount = 2m, date = "2026-06-12", description = "Newest" });

        JsonNode? json = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));

        await Assert.That(json!["items"]!.AsArray()[0]!["description"]!.GetValue<string>()).IsEqualTo("Newest");
        await Assert.That(json["items"]!.AsArray()[1]!["description"]!.GetValue<string>()).IsEqualTo("Older");
    }

    [Test]
    public async Task GetTransactions_WhenEmpty_ReturnsEmptyItemsArray()
    {
        await using PostgresTestHost host = await StartHostAsync();
        JsonNode? json =
            await JsonNode.ParseAsync(await host.Factory.CreateAuthenticatedClient().GetStreamAsync("/api/transactions"));

        await Assert.That(json!["items"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task PostThenGet_PreservesDecimalAndDateFidelity()
    {
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync("/api/transactions",
            new { amount = -42.50m, date = "2026-06-12", description = "Groceries" });

        JsonNode? json = await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions"));
        JsonNode item = json!["items"]!.AsArray()[0]!;

        await Assert.That(item["amount"]!.GetValue<decimal>()).IsEqualTo(-42.50m);
        await Assert.That(item["date"]!.GetValue<string>()).IsEqualTo("2026-06-12");
    }

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
