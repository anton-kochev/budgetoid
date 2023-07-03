using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;

namespace Domain.Entities;

public sealed class Transaction : TableEntity
{
    [JsonProperty("id")] public string Id { get; init; } = Guid.NewGuid().ToString();
    [JsonProperty("accountId")] public string AccountId { get; init; } = Guid.Empty.ToString();
    public DateTime CreatedOn { get; init; } = DateTime.UtcNow;
    public decimal Amount { get; init; }
    public DateTime Date { get; init; }
    public Guid PayeeId { get; init; } = Guid.Empty;
    public Guid CategoryId { get; init; } = Guid.Empty;
    public string Comment { get; init; } = string.Empty;
    public string[] Tags { get; init; } = Array.Empty<string>();
}
