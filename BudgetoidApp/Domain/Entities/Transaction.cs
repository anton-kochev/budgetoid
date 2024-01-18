using Domain.Common;
using Newtonsoft.Json;

namespace Domain.Entities;

public sealed class Transaction
{
    [JsonProperty("id")] public string Id { get; init; } = Guid.NewGuid().ToString();
    [JsonProperty("userId")] public string UserId { get; init; } = Guid.Empty.ToString();
    public Guid AccountId { get; init; } = Guid.Empty;
    public decimal Amount { get; init; } = 0.0m;
    public Guid CategoryId { get; init; } = Guid.Empty;
    public string Comment { get; init; } = string.Empty;
    public DateTime Date { get; init; } = DateTime.UtcNow.ToDateOnly().ToDateTime();
    public string Payee { get; init; } = string.Empty;
    public string[] Tags { get; init; } = Array.Empty<string>();
}
