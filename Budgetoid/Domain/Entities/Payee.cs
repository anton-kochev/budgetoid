using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;

namespace Budgetoid.Domain.Entities;

public sealed class Payee : TableEntity
{
    // Name is a unique identifier for a payee
    [JsonProperty("name")] public string Name { get; init; } = string.Empty;
    [JsonProperty("userId")] public string UserId { get; init; } = Guid.Empty.ToString();
    public string GeoLocation { get; init; } = string.Empty;
}
