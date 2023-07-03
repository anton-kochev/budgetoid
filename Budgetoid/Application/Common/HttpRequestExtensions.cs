using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Application.Common;

public static class HttpRequestExtensions
{
    public static async Task<T?> DeserializeBodyAsync<T>(this HttpRequest req)
    {
        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(requestBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
