using System.Text.Json.Serialization;

namespace Domain.Entities;

public sealed class Payee
{
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString();
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("userId")] public string UserId { get; init; } = Guid.Empty.ToString();
    public string GeoLocation { get; init; } = string.Empty;
}
