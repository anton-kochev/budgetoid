using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;

namespace Budgetoid.Domain.Entities;

public sealed class Transaction : TableEntity
{
    [JsonProperty("id")] public string Id { get; init; } = Guid.NewGuid().ToString();

    [JsonProperty("accountId")] public string AccountId { get; init; } = Guid.Empty.ToString();
    public decimal Amount { get; init; } = 0.0m;
    public DateTime Date { get; init; } = DateTime.UtcNow.ToDateOnly().ToDateTime();
    public Guid PayeeId { get; init; } = Guid.Empty;
    public Guid CategoryId { get; init; } = Guid.Empty;
    public string Comment { get; init; } = string.Empty;
    public string[] Tags { get; init; } = Array.Empty<string>();
}
