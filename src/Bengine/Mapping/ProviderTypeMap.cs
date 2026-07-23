namespace Bengine.Mapping;

using System.Collections.Frozen;
using Bengine.Enums;

/// <summary>
/// Bidirectional mapping between logical <see cref="DbDataType"/> values and the
/// native type spellings of each <see cref="DatabaseProvider"/>. Forward maps
/// resolve <c>DbType → Provider</c>; reverse maps resolve
/// <c>Provider → DbType</c> using a curated canonical choice where several
/// logical types share one native spelling. All maps are immutable.
/// </summary>
public static class ProviderTypeMap
{
    private static readonly FrozenDictionary<DatabaseProvider, FrozenDictionary<DbDataType, string>> ForwardMaps =
        BuildForward();

    private static readonly FrozenDictionary<DatabaseProvider, FrozenDictionary<string, DbDataType>> ReverseMaps =
        BuildReverse();

    /// <summary>Resolves the native type name of a logical type for a provider.</summary>
    /// <param name="provider">The target provider.</param>
    /// <param name="dataType">The logical type to render.</param>
    /// <returns>The provider's native base type name.</returns>
    /// <exception cref="KeyNotFoundException">The provider does not support <paramref name="dataType"/>.</exception>
    public static string ToNative(DatabaseProvider provider, DbDataType dataType) =>
        ForwardMaps[provider].TryGetValue(dataType, out var native)
            ? native
            : throw new KeyNotFoundException($"'{provider}' has no native mapping for '{dataType}'.");

    /// <summary>Attempts to resolve the native type name of a logical type for a provider.</summary>
    /// <param name="provider">The target provider.</param>
    /// <param name="dataType">The logical type to render.</param>
    /// <param name="native">The native base type name when supported.</param>
    /// <returns><c>true</c> if the provider supports the type; otherwise <c>false</c>.</returns>
    public static bool TryToNative(DatabaseProvider provider, DbDataType dataType, out string? native) =>
        ForwardMaps[provider].TryGetValue(dataType, out native);

    /// <summary>Attempts to resolve the logical type behind a provider's native type name.</summary>
    /// <param name="provider">The source provider.</param>
    /// <param name="nativeName">The native type name (case-insensitive).</param>
    /// <param name="dataType">The resolved logical type when recognized.</param>
    /// <returns><c>true</c> if the native name is recognized; otherwise <c>false</c>.</returns>
    public static bool TryFromNative(DatabaseProvider provider, string nativeName, out DbDataType dataType) =>
        ReverseMaps[provider].TryGetValue(nativeName, out dataType);

    private static FrozenDictionary<DatabaseProvider, FrozenDictionary<DbDataType, string>> BuildForward() =>
        new Dictionary<DatabaseProvider, FrozenDictionary<DbDataType, string>>
        {
            [DatabaseProvider.PostgreSql] = new Dictionary<DbDataType, string>
            {
                [DbDataType.Boolean] = "boolean",
                [DbDataType.TinyInt] = "smallint",
                [DbDataType.SmallInt] = "smallint",
                [DbDataType.Integer] = "integer",
                [DbDataType.BigInt] = "bigint",
                [DbDataType.Decimal] = "numeric",
                [DbDataType.Real] = "real",
                [DbDataType.Double] = "double precision",
                [DbDataType.Char] = "char",
                [DbDataType.VarChar] = "varchar",
                [DbDataType.Text] = "text",
                [DbDataType.Binary] = "bytea",
                [DbDataType.VarBinary] = "bytea",
                [DbDataType.Blob] = "bytea",
                [DbDataType.Date] = "date",
                [DbDataType.Time] = "time",
                [DbDataType.Timestamp] = "timestamp",
                [DbDataType.TimestampTz] = "timestamptz",
                [DbDataType.Uuid] = "uuid",
                [DbDataType.Json] = "json",
                [DbDataType.JsonBinary] = "jsonb",
                [DbDataType.Xml] = "xml",
            }.ToFrozenDictionary(),

            [DatabaseProvider.SqlServer] = new Dictionary<DbDataType, string>
            {
                [DbDataType.Boolean] = "bit",
                [DbDataType.TinyInt] = "tinyint",
                [DbDataType.SmallInt] = "smallint",
                [DbDataType.Integer] = "int",
                [DbDataType.BigInt] = "bigint",
                [DbDataType.Decimal] = "decimal",
                [DbDataType.Real] = "real",
                [DbDataType.Double] = "float",
                [DbDataType.Char] = "char",
                [DbDataType.VarChar] = "varchar",
                [DbDataType.Text] = "varchar(max)",
                [DbDataType.Binary] = "binary",
                [DbDataType.VarBinary] = "varbinary",
                [DbDataType.Blob] = "varbinary(max)",
                [DbDataType.Date] = "date",
                [DbDataType.Time] = "time",
                [DbDataType.Timestamp] = "datetime2",
                [DbDataType.TimestampTz] = "datetimeoffset",
                [DbDataType.Uuid] = "uniqueidentifier",
                [DbDataType.Json] = "nvarchar(max)",
                [DbDataType.Xml] = "xml",
            }.ToFrozenDictionary(),

            [DatabaseProvider.MySql] = new Dictionary<DbDataType, string>
            {
                [DbDataType.Boolean] = "tinyint(1)",
                [DbDataType.TinyInt] = "tinyint",
                [DbDataType.SmallInt] = "smallint",
                [DbDataType.Integer] = "int",
                [DbDataType.BigInt] = "bigint",
                [DbDataType.Decimal] = "decimal",
                [DbDataType.Real] = "float",
                [DbDataType.Double] = "double",
                [DbDataType.Char] = "char",
                [DbDataType.VarChar] = "varchar",
                [DbDataType.Text] = "text",
                [DbDataType.Binary] = "binary",
                [DbDataType.VarBinary] = "varbinary",
                [DbDataType.Blob] = "blob",
                [DbDataType.Date] = "date",
                [DbDataType.Time] = "time",
                [DbDataType.Timestamp] = "datetime",
                [DbDataType.TimestampTz] = "timestamp",
                [DbDataType.Uuid] = "char(36)",
                [DbDataType.Json] = "json",
            }.ToFrozenDictionary(),

            [DatabaseProvider.Sqlite] = new Dictionary<DbDataType, string>
            {
                [DbDataType.Boolean] = "INTEGER",
                [DbDataType.TinyInt] = "INTEGER",
                [DbDataType.SmallInt] = "INTEGER",
                [DbDataType.Integer] = "INTEGER",
                [DbDataType.BigInt] = "INTEGER",
                [DbDataType.Decimal] = "NUMERIC",
                [DbDataType.Real] = "REAL",
                [DbDataType.Double] = "REAL",
                [DbDataType.Char] = "TEXT",
                [DbDataType.VarChar] = "TEXT",
                [DbDataType.Text] = "TEXT",
                [DbDataType.Binary] = "BLOB",
                [DbDataType.VarBinary] = "BLOB",
                [DbDataType.Blob] = "BLOB",
                [DbDataType.Date] = "TEXT",
                [DbDataType.Time] = "TEXT",
                [DbDataType.Timestamp] = "TEXT",
                [DbDataType.TimestampTz] = "TEXT",
                [DbDataType.Uuid] = "TEXT",
                [DbDataType.Json] = "TEXT",
            }.ToFrozenDictionary(),
        }.ToFrozenDictionary();

    private static FrozenDictionary<DatabaseProvider, FrozenDictionary<string, DbDataType>> BuildReverse() =>
        new Dictionary<DatabaseProvider, FrozenDictionary<string, DbDataType>>
        {
            [DatabaseProvider.PostgreSql] = Reverse(
                ("boolean", DbDataType.Boolean),
                ("smallint", DbDataType.SmallInt),
                ("integer", DbDataType.Integer),
                ("bigint", DbDataType.BigInt),
                ("numeric", DbDataType.Decimal),
                ("real", DbDataType.Real),
                ("double precision", DbDataType.Double),
                ("char", DbDataType.Char),
                ("varchar", DbDataType.VarChar),
                ("text", DbDataType.Text),
                ("bytea", DbDataType.Blob),
                ("date", DbDataType.Date),
                ("time", DbDataType.Time),
                ("timestamp", DbDataType.Timestamp),
                ("timestamptz", DbDataType.TimestampTz),
                ("uuid", DbDataType.Uuid),
                ("json", DbDataType.Json),
                ("jsonb", DbDataType.JsonBinary),
                ("xml", DbDataType.Xml)),

            [DatabaseProvider.SqlServer] = Reverse(
                ("bit", DbDataType.Boolean),
                ("tinyint", DbDataType.TinyInt),
                ("smallint", DbDataType.SmallInt),
                ("int", DbDataType.Integer),
                ("bigint", DbDataType.BigInt),
                ("decimal", DbDataType.Decimal),
                ("real", DbDataType.Real),
                ("float", DbDataType.Double),
                ("char", DbDataType.Char),
                ("varchar", DbDataType.VarChar),
                ("varchar(max)", DbDataType.Text),
                ("binary", DbDataType.Binary),
                ("varbinary", DbDataType.VarBinary),
                ("varbinary(max)", DbDataType.Blob),
                ("date", DbDataType.Date),
                ("time", DbDataType.Time),
                ("datetime2", DbDataType.Timestamp),
                ("datetimeoffset", DbDataType.TimestampTz),
                ("uniqueidentifier", DbDataType.Uuid),
                ("nvarchar(max)", DbDataType.Json),
                ("xml", DbDataType.Xml)),

            [DatabaseProvider.MySql] = Reverse(
                ("tinyint(1)", DbDataType.Boolean),
                ("tinyint", DbDataType.TinyInt),
                ("smallint", DbDataType.SmallInt),
                ("int", DbDataType.Integer),
                ("bigint", DbDataType.BigInt),
                ("decimal", DbDataType.Decimal),
                ("float", DbDataType.Real),
                ("double", DbDataType.Double),
                ("char", DbDataType.Char),
                ("varchar", DbDataType.VarChar),
                ("text", DbDataType.Text),
                ("binary", DbDataType.Binary),
                ("varbinary", DbDataType.VarBinary),
                ("blob", DbDataType.Blob),
                ("date", DbDataType.Date),
                ("time", DbDataType.Time),
                ("datetime", DbDataType.Timestamp),
                ("timestamp", DbDataType.TimestampTz),
                ("char(36)", DbDataType.Uuid),
                ("json", DbDataType.Json)),

            [DatabaseProvider.Sqlite] = Reverse(
                ("INTEGER", DbDataType.Integer),
                ("NUMERIC", DbDataType.Decimal),
                ("REAL", DbDataType.Double),
                ("TEXT", DbDataType.Text),
                ("BLOB", DbDataType.Blob)),
        }.ToFrozenDictionary();

    private static FrozenDictionary<string, DbDataType> Reverse(params (string Native, DbDataType Type)[] pairs) =>
        pairs.ToFrozenDictionary(p => p.Native, p => p.Type, StringComparer.OrdinalIgnoreCase);
}
