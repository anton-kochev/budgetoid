using System;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;

namespace Budgetoid.Models;

/// <summary>
///     ISO_4217 three letters currency code
/// </summary>
public sealed class Account : TableEntity
{
    [JsonProperty("id")] public string Id { get; init; } = Guid.NewGuid().ToString();
    [JsonProperty("userId")] public string UserId { get; init; } = Guid.Empty.ToString();
    public string Currency { get; init; } = "USD";
    public string Name { get; init; } = string.Empty;
}
