namespace Bengine.Serialization;

using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Enables <see cref="System.Text.Json"/> to round-trip
/// <see cref="FrozenDictionary{TKey, TValue}"/>. The built-in serializer can
/// write a frozen dictionary but cannot construct one on read because it is
/// immutable; this factory bridges the gap by materializing a mutable dictionary
/// and freezing it.
/// </summary>
public sealed class FrozenDictionaryConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType
        && typeToConvert.GetGenericTypeDefinition() == typeof(FrozenDictionary<,>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var arguments = typeToConvert.GetGenericArguments();
        var converterType = typeof(FrozenDictionaryConverter<,>).MakeGenericType(arguments);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class FrozenDictionaryConverter<TKey, TValue>
        : JsonConverter<FrozenDictionary<TKey, TValue>>
        where TKey : notnull
    {
        public override FrozenDictionary<TKey, TValue> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var buffer = JsonSerializer.Deserialize<Dictionary<TKey, TValue>>(ref reader, options);
            return buffer is null
                ? FrozenDictionary<TKey, TValue>.Empty
                : buffer.ToFrozenDictionary();
        }

        public override void Write(
            Utf8JsonWriter writer, FrozenDictionary<TKey, TValue> value, JsonSerializerOptions options)
        {
            // Copy into a plain dictionary so the built-in converter handles the
            // write; serializing the frozen instance directly would re-enter this
            // converter and recurse without end.
            var buffer = new Dictionary<TKey, TValue>(value);
            JsonSerializer.Serialize(writer, buffer, options);
        }
    }
}
