using System.Text.Json.Serialization;

namespace Domain.Entities;

/// <summary>
///     ISO_4217 three letters currency code
/// </summary>
public sealed class Account
{
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString();
    [JsonPropertyName("userId")] public string UserId { get; init; } = Guid.Empty.ToString();
    public string Comment { get; init; } = string.Empty;
    public string Currency { get; init; } = "USD";
    public string Name { get; init; } = string.Empty;
}
