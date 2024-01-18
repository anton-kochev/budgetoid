using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;

namespace Api.Common;

public static class HttpRequestExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<T?> DeserializeBodyAsync<T>(this HttpRequestData request)
    {
        string requestBody = await new StreamReader(request.Body).ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(requestBody, SerializerOptions);
    }

    public static bool TryDeserializeBody<T>(this HttpRequestData request, out T? body)
    {
        body = default;
        try
        {
            body = request.DeserializeBodyAsync<T>().Result;
        }
        catch (Exception)
        {
            return false;
        }

        return body is not null;
    }
}