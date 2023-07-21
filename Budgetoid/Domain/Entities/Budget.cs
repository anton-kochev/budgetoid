using Newtonsoft.Json;

namespace Budgetoid.Domain.Entities;

public sealed class Budget
{
    [JsonProperty("id")] public string Id { get; init; } = Guid.NewGuid().ToString();
    [JsonProperty("name")] public string Name { get; init; } = string.Empty;
    [JsonProperty("userId")] public string UserId { get; init; } = Guid.Empty.ToString();
}
