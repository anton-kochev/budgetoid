using Application.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Infrastructure;

/// <summary>
/// Reads and writes <see cref="Optional{T}"/> for every closed <c>T</c>.
/// </summary>
/// <remarks>
/// It lives in the API layer rather than beside <see cref="Optional{T}"/> because serialization is a
/// transport concern: <see cref="Optional{T}"/> describes a partial update, not a wire format.
/// Registering the converter on the HTTP JSON options instead of hanging a <c>[JsonConverter]</c>
/// attribute on the Application type is what keeps that boundary.
/// </remarks>
public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type valueType = typeToConvert.GetGenericArguments()[0];

        return (JsonConverter)Activator.CreateInstance(
            typeof(OptionalJsonConverter<>).MakeGenericType(valueType))!;
    }

    private sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
    {
        // Routes a null token into Read rather than letting the serializer resolve it. Optional<T>
        // is a struct, so the framework's computed default already does this — but that default is
        // derived from Optional<T> being a non-nullable value type, not from anything about partial
        // updates, and it inverts if Optional<T> ever becomes a class. Overriding this to false is
        // what breaks the feature: every explicit null then fails with "The JSON value could not be
        // converted to Optional<T>", so clearing a field would 400 instead of clearing it. Stated
        // here so the behaviour is visible at the call site instead of inferred from the type.
        public override bool HandleNull => true;

        public override Optional<T> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            // A null token produces a *set* Optional, never an absent one: set-to-null is a value.
            // Deserializing rather than short-circuiting on the null token is also what rejects a
            // null for a non-nullable T — Deserialize<decimal> against a null token throws
            // JsonException, which minimal-API body binding reports as a 400. That falls out of the
            // mechanism, so amount, date and accountId need no special case.
            return new Optional<T>(JsonSerializer.Deserialize<T>(ref reader, options));
        }

        public override void Write(
            Utf8JsonWriter writer,
            Optional<T> value,
            JsonSerializerOptions options)
        {
            // Nothing on the request path serializes an Optional, but the type is reachable from
            // OpenAPI schema generation, so a throwing Write would be a landmine for no benefit.
            // Absence has no representation in a property-value position, so it writes as null.
            T? inner = value.Value;
            if (inner is null)
            {
                writer.WriteNullValue();
                return;
            }

            JsonSerializer.Serialize(writer, inner, options);
        }
    }
}
