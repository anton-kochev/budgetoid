using System.Text.Json.Serialization;

namespace Domain.Entities;

public sealed class User
{
    [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
    public Guid Id { get; init; } = Guid.NewGuid();
}
