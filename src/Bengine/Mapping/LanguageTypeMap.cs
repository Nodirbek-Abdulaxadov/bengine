namespace Bengine.Mapping;

using System.Collections.Frozen;
using Bengine.Enums;

/// <summary>
/// Bidirectional mapping between logical <see cref="DbDataType"/> values and the
/// idiomatic type spellings of each target <see cref="Language"/>. Forward maps
/// resolve <c>DbType → Language</c>; reverse maps resolve
/// <c>Language → DbType</c> using a curated canonical choice where several
/// logical types share one language type. All maps are immutable.
/// </summary>
public static class LanguageTypeMap
{
    private static readonly FrozenDictionary<Language, FrozenDictionary<DbDataType, string>> ForwardMaps =
        BuildForward();

    private static readonly FrozenDictionary<Language, FrozenDictionary<string, DbDataType>> ReverseMaps =
        BuildReverse();

    /// <summary>Resolves the idiomatic language type of a logical type.</summary>
    /// <param name="language">The target language.</param>
    /// <param name="dataType">The logical type to render.</param>
    /// <returns>The language's idiomatic type name.</returns>
    /// <exception cref="KeyNotFoundException">The language cannot represent <paramref name="dataType"/>.</exception>
    public static string ToNative(Language language, DbDataType dataType) =>
        ForwardMaps[language].TryGetValue(dataType, out var native)
            ? native
            : throw new KeyNotFoundException($"'{language}' has no mapping for '{dataType}'.");

    /// <summary>Attempts to resolve the idiomatic language type of a logical type.</summary>
    /// <param name="language">The target language.</param>
    /// <param name="dataType">The logical type to render.</param>
    /// <param name="native">The language type name when representable.</param>
    /// <returns><c>true</c> if the language can represent the type; otherwise <c>false</c>.</returns>
    public static bool TryToNative(Language language, DbDataType dataType, out string? native) =>
        ForwardMaps[language].TryGetValue(dataType, out native);

    /// <summary>Attempts to resolve the logical type behind a language type name.</summary>
    /// <param name="language">The source language.</param>
    /// <param name="nativeName">The language type name (case-sensitive per language conventions).</param>
    /// <param name="dataType">The resolved logical type when recognized.</param>
    /// <returns><c>true</c> if the language type is recognized; otherwise <c>false</c>.</returns>
    public static bool TryFromNative(Language language, string nativeName, out DbDataType dataType) =>
        ReverseMaps[language].TryGetValue(nativeName, out dataType);

    private static FrozenDictionary<Language, FrozenDictionary<DbDataType, string>> BuildForward() =>
        new Dictionary<Language, FrozenDictionary<DbDataType, string>>
        {
            [Language.CSharp] = Forward(
                (DbDataType.Boolean, "bool"), (DbDataType.TinyInt, "sbyte"),
                (DbDataType.SmallInt, "short"), (DbDataType.Integer, "int"),
                (DbDataType.BigInt, "long"), (DbDataType.Decimal, "decimal"),
                (DbDataType.Real, "float"), (DbDataType.Double, "double"),
                (DbDataType.Char, "string"), (DbDataType.VarChar, "string"),
                (DbDataType.Text, "string"), (DbDataType.Binary, "byte[]"),
                (DbDataType.VarBinary, "byte[]"), (DbDataType.Blob, "byte[]"),
                (DbDataType.Date, "DateOnly"), (DbDataType.Time, "TimeOnly"),
                (DbDataType.Timestamp, "DateTime"), (DbDataType.TimestampTz, "DateTimeOffset"),
                (DbDataType.Uuid, "Guid"), (DbDataType.Json, "string"),
                (DbDataType.JsonBinary, "string"), (DbDataType.Xml, "string")),

            [Language.Go] = Forward(
                (DbDataType.Boolean, "bool"), (DbDataType.TinyInt, "int8"),
                (DbDataType.SmallInt, "int16"), (DbDataType.Integer, "int32"),
                (DbDataType.BigInt, "int64"), (DbDataType.Decimal, "string"),
                (DbDataType.Real, "float32"), (DbDataType.Double, "float64"),
                (DbDataType.Char, "string"), (DbDataType.VarChar, "string"),
                (DbDataType.Text, "string"), (DbDataType.Binary, "[]byte"),
                (DbDataType.VarBinary, "[]byte"), (DbDataType.Blob, "[]byte"),
                (DbDataType.Date, "time.Time"), (DbDataType.Time, "time.Time"),
                (DbDataType.Timestamp, "time.Time"), (DbDataType.TimestampTz, "time.Time"),
                (DbDataType.Uuid, "string"), (DbDataType.Json, "json.RawMessage"),
                (DbDataType.JsonBinary, "json.RawMessage"), (DbDataType.Xml, "string")),

            [Language.Rust] = Forward(
                (DbDataType.Boolean, "bool"), (DbDataType.TinyInt, "i8"),
                (DbDataType.SmallInt, "i16"), (DbDataType.Integer, "i32"),
                (DbDataType.BigInt, "i64"), (DbDataType.Decimal, "rust_decimal::Decimal"),
                (DbDataType.Real, "f32"), (DbDataType.Double, "f64"),
                (DbDataType.Char, "String"), (DbDataType.VarChar, "String"),
                (DbDataType.Text, "String"), (DbDataType.Binary, "Vec<u8>"),
                (DbDataType.VarBinary, "Vec<u8>"), (DbDataType.Blob, "Vec<u8>"),
                (DbDataType.Date, "chrono::NaiveDate"), (DbDataType.Time, "chrono::NaiveTime"),
                (DbDataType.Timestamp, "chrono::NaiveDateTime"),
                (DbDataType.TimestampTz, "chrono::DateTime<chrono::Utc>"),
                (DbDataType.Uuid, "uuid::Uuid"), (DbDataType.Json, "serde_json::Value"),
                (DbDataType.JsonBinary, "serde_json::Value"), (DbDataType.Xml, "String")),

            [Language.TypeScript] = Forward(
                (DbDataType.Boolean, "boolean"), (DbDataType.TinyInt, "number"),
                (DbDataType.SmallInt, "number"), (DbDataType.Integer, "number"),
                (DbDataType.BigInt, "bigint"), (DbDataType.Decimal, "string"),
                (DbDataType.Real, "number"), (DbDataType.Double, "number"),
                (DbDataType.Char, "string"), (DbDataType.VarChar, "string"),
                (DbDataType.Text, "string"), (DbDataType.Binary, "Uint8Array"),
                (DbDataType.VarBinary, "Uint8Array"), (DbDataType.Blob, "Uint8Array"),
                (DbDataType.Date, "Date"), (DbDataType.Time, "string"),
                (DbDataType.Timestamp, "Date"), (DbDataType.TimestampTz, "Date"),
                (DbDataType.Uuid, "string"), (DbDataType.Json, "unknown"),
                (DbDataType.JsonBinary, "unknown"), (DbDataType.Xml, "string")),

            [Language.Python] = Forward(
                (DbDataType.Boolean, "bool"), (DbDataType.TinyInt, "int"),
                (DbDataType.SmallInt, "int"), (DbDataType.Integer, "int"),
                (DbDataType.BigInt, "int"), (DbDataType.Decimal, "decimal.Decimal"),
                (DbDataType.Real, "float"), (DbDataType.Double, "float"),
                (DbDataType.Char, "str"), (DbDataType.VarChar, "str"),
                (DbDataType.Text, "str"), (DbDataType.Binary, "bytes"),
                (DbDataType.VarBinary, "bytes"), (DbDataType.Blob, "bytes"),
                (DbDataType.Date, "datetime.date"), (DbDataType.Time, "datetime.time"),
                (DbDataType.Timestamp, "datetime.datetime"),
                (DbDataType.TimestampTz, "datetime.datetime"),
                (DbDataType.Uuid, "uuid.UUID"), (DbDataType.Json, "dict"),
                (DbDataType.JsonBinary, "dict"), (DbDataType.Xml, "str")),

            [Language.Php] = Forward(
                (DbDataType.Boolean, "bool"), (DbDataType.TinyInt, "int"),
                (DbDataType.SmallInt, "int"), (DbDataType.Integer, "int"),
                (DbDataType.BigInt, "int"), (DbDataType.Decimal, "string"),
                (DbDataType.Real, "float"), (DbDataType.Double, "float"),
                (DbDataType.Char, "string"), (DbDataType.VarChar, "string"),
                (DbDataType.Text, "string"), (DbDataType.Binary, "string"),
                (DbDataType.VarBinary, "string"), (DbDataType.Blob, "string"),
                (DbDataType.Date, "\\DateTimeInterface"), (DbDataType.Time, "\\DateTimeInterface"),
                (DbDataType.Timestamp, "\\DateTimeInterface"),
                (DbDataType.TimestampTz, "\\DateTimeInterface"),
                (DbDataType.Uuid, "string"), (DbDataType.Json, "array"),
                (DbDataType.JsonBinary, "array"), (DbDataType.Xml, "string")),
        }.ToFrozenDictionary();

    private static FrozenDictionary<Language, FrozenDictionary<string, DbDataType>> BuildReverse() =>
        new Dictionary<Language, FrozenDictionary<string, DbDataType>>
        {
            [Language.CSharp] = Reverse(
                ("bool", DbDataType.Boolean), ("sbyte", DbDataType.TinyInt),
                ("short", DbDataType.SmallInt), ("int", DbDataType.Integer),
                ("long", DbDataType.BigInt), ("decimal", DbDataType.Decimal),
                ("float", DbDataType.Real), ("double", DbDataType.Double),
                ("string", DbDataType.VarChar), ("byte[]", DbDataType.Blob),
                ("DateOnly", DbDataType.Date), ("TimeOnly", DbDataType.Time),
                ("DateTime", DbDataType.Timestamp), ("DateTimeOffset", DbDataType.TimestampTz),
                ("Guid", DbDataType.Uuid)),

            [Language.Go] = Reverse(
                ("bool", DbDataType.Boolean), ("int8", DbDataType.TinyInt),
                ("int16", DbDataType.SmallInt), ("int32", DbDataType.Integer),
                ("int64", DbDataType.BigInt), ("float32", DbDataType.Real),
                ("float64", DbDataType.Double), ("string", DbDataType.VarChar),
                ("[]byte", DbDataType.Blob), ("time.Time", DbDataType.Timestamp),
                ("json.RawMessage", DbDataType.Json)),

            [Language.Rust] = Reverse(
                ("bool", DbDataType.Boolean), ("i8", DbDataType.TinyInt),
                ("i16", DbDataType.SmallInt), ("i32", DbDataType.Integer),
                ("i64", DbDataType.BigInt), ("rust_decimal::Decimal", DbDataType.Decimal),
                ("f32", DbDataType.Real), ("f64", DbDataType.Double),
                ("String", DbDataType.VarChar), ("Vec<u8>", DbDataType.Blob),
                ("chrono::NaiveDate", DbDataType.Date), ("chrono::NaiveTime", DbDataType.Time),
                ("chrono::NaiveDateTime", DbDataType.Timestamp),
                ("chrono::DateTime<chrono::Utc>", DbDataType.TimestampTz),
                ("uuid::Uuid", DbDataType.Uuid), ("serde_json::Value", DbDataType.Json)),

            [Language.TypeScript] = Reverse(
                ("boolean", DbDataType.Boolean), ("number", DbDataType.Integer),
                ("bigint", DbDataType.BigInt), ("string", DbDataType.VarChar),
                ("Uint8Array", DbDataType.Blob), ("Date", DbDataType.Timestamp),
                ("unknown", DbDataType.Json)),

            [Language.Python] = Reverse(
                ("bool", DbDataType.Boolean), ("int", DbDataType.Integer),
                ("decimal.Decimal", DbDataType.Decimal), ("float", DbDataType.Double),
                ("str", DbDataType.VarChar), ("bytes", DbDataType.Blob),
                ("datetime.date", DbDataType.Date), ("datetime.time", DbDataType.Time),
                ("datetime.datetime", DbDataType.Timestamp), ("uuid.UUID", DbDataType.Uuid),
                ("dict", DbDataType.Json)),

            [Language.Php] = Reverse(
                ("bool", DbDataType.Boolean), ("int", DbDataType.Integer),
                ("float", DbDataType.Double), ("string", DbDataType.VarChar),
                ("\\DateTimeInterface", DbDataType.Timestamp), ("array", DbDataType.Json)),
        }.ToFrozenDictionary();

    private static FrozenDictionary<DbDataType, string> Forward(params (DbDataType Type, string Native)[] pairs) =>
        pairs.ToFrozenDictionary(p => p.Type, p => p.Native);

    private static FrozenDictionary<string, DbDataType> Reverse(params (string Native, DbDataType Type)[] pairs) =>
        pairs.ToFrozenDictionary(p => p.Native, p => p.Type, StringComparer.Ordinal);
}
