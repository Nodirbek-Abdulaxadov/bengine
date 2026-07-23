namespace Bengine.Serialization;

using System.Text.Json;
using System.Text.Json.Serialization;
using Bengine.Models;

/// <summary>
/// Round-trips a <see cref="DatabaseSchema"/> AST to and from JSON. The AST
/// remains the single in-memory source of truth; JSON is used only for
/// persistence and for exchange with an external designer that produces a
/// <c>schema.json</c> document. Enums serialize by name and immutable
/// <c>FrozenDictionary</c> values are handled by dedicated converters.
/// </summary>
public static class SchemaSerializer
{
    /// <summary>Gets the shared, immutable serializer options used for every operation.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Serializes a schema to an indented JSON document.</summary>
    /// <param name="schema">The schema to serialize.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(DatabaseSchema schema) =>
        JsonSerializer.Serialize(schema, Options);

    /// <summary>Deserializes a schema from a JSON document.</summary>
    /// <param name="json">The JSON representation.</param>
    /// <returns>The reconstructed schema AST.</returns>
    /// <exception cref="JsonException">The JSON is malformed or missing required members.</exception>
    /// <exception cref="InvalidOperationException">The JSON deserializes to <c>null</c>.</exception>
    public static DatabaseSchema FromJson(string json) =>
        JsonSerializer.Deserialize<DatabaseSchema>(json, Options)
        ?? throw new InvalidOperationException("The JSON document deserialized to a null schema.");

    /// <summary>Writes a schema to a JSON file, overwriting any existing file.</summary>
    /// <param name="schema">The schema to persist.</param>
    /// <param name="path">The destination file path.</param>
    public static void Save(DatabaseSchema schema, string path) =>
        File.WriteAllText(path, ToJson(schema));

    /// <summary>Reads a schema from a JSON file.</summary>
    /// <param name="path">The source file path.</param>
    /// <returns>The reconstructed schema AST.</returns>
    public static DatabaseSchema Load(string path) =>
        FromJson(File.ReadAllText(path));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // Computed getters (IsNullable, IsUnique, ...) have no setter and are
            // derived, so they are excluded from output and never round-tripped.
            IgnoreReadOnlyProperties = true,
        };

        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new FrozenDictionaryConverterFactory());
        return options;
    }
}
