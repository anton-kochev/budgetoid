using System.Text.Json.Serialization;

namespace Domain.Entities;

public sealed class Budget
{
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString();
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("userId")] public string UserId { get; init; } = Guid.Empty.ToString();
}
