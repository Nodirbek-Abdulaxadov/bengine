namespace Bengine.Metadata;

using System.Collections.Frozen;
using Bengine.Constants;
using Bengine.Enums;

/// <summary>
/// The authoritative registry of <see cref="DbDataTypeMetadata"/> for every
/// logical <see cref="DbDataType"/>. This is the single source of truth consumed
/// by mapping, validation, and generation; no other component hardcodes type
/// facts.
/// </summary>
public static class DbDataTypeRegistry
{
    private static readonly FrozenDictionary<DbDataType, DbDataTypeMetadata> Entries = Build();

    /// <summary>Gets metadata for every registered logical type.</summary>
    public static IReadOnlyCollection<DbDataTypeMetadata> All => Entries.Values;

    /// <summary>Gets the metadata for a logical type.</summary>
    /// <param name="dataType">The type to resolve.</param>
    /// <returns>The type's metadata.</returns>
    /// <exception cref="KeyNotFoundException">No metadata is registered for <paramref name="dataType"/>.</exception>
    public static DbDataTypeMetadata Get(DbDataType dataType) =>
        Entries.TryGetValue(dataType, out var metadata)
            ? metadata
            : throw new KeyNotFoundException($"No metadata registered for '{dataType}'.");

    /// <summary>Attempts to resolve the metadata for a logical type.</summary>
    /// <param name="dataType">The type to resolve.</param>
    /// <param name="metadata">The resolved metadata when present.</param>
    /// <returns><c>true</c> if metadata exists; otherwise <c>false</c>.</returns>
    public static bool TryGet(DbDataType dataType, out DbDataTypeMetadata? metadata) =>
        Entries.TryGetValue(dataType, out metadata);

    private static FrozenDictionary<DbDataType, DbDataTypeMetadata> Build()
    {
        var all = SchemaConstants.AllProviders;
        var langs = SchemaConstants.AllLanguages;
        var numeric = new[] { ValidationKind.Numeric }.ToFrozenSet();
        var stringVal = new[] { ValidationKind.String }.ToFrozenSet();
        var dateVal = new[] { ValidationKind.Date }.ToFrozenSet();
        var binaryVal = new[] { ValidationKind.Binary }.ToFrozenSet();
        var noVal = FrozenSet<ValidationKind>.Empty;

        var list = new[]
        {
            Meta(DbDataType.Boolean, TypeCategory.Boolean, TypeCapability.None,
                ValidationKind.None, all, langs, noVal, DbTypeLimits.None),

            Meta(DbDataType.TinyInt, TypeCategory.Integer,
                TypeCapability.Unsigned | TypeCapability.AutoIncrement,
                ValidationKind.Numeric, all, langs, numeric,
                new DbTypeLimits { MinValue = -128m, MaxValue = 127m }),

            Meta(DbDataType.SmallInt, TypeCategory.Integer,
                TypeCapability.Unsigned | TypeCapability.AutoIncrement,
                ValidationKind.Numeric, all, langs, numeric,
                new DbTypeLimits { MinValue = -32_768m, MaxValue = 32_767m }),

            Meta(DbDataType.Integer, TypeCategory.Integer,
                TypeCapability.Unsigned | TypeCapability.AutoIncrement,
                ValidationKind.Numeric, all, langs, numeric,
                new DbTypeLimits { MinValue = -2_147_483_648m, MaxValue = 2_147_483_647m }),

            Meta(DbDataType.BigInt, TypeCategory.Integer,
                TypeCapability.Unsigned | TypeCapability.AutoIncrement,
                ValidationKind.Numeric, all, langs, numeric,
                new DbTypeLimits
                {
                    MinValue = -9_223_372_036_854_775_808m,
                    MaxValue = 9_223_372_036_854_775_807m,
                }),

            Meta(DbDataType.Decimal, TypeCategory.Numeric,
                TypeCapability.Precision | TypeCapability.Scale,
                ValidationKind.Numeric, all, langs, numeric,
                new DbTypeLimits
                {
                    DefaultPrecision = 18,
                    MaxPrecision = 38,
                    DefaultScale = 0,
                    MaxScale = 38,
                }),

            Meta(DbDataType.Real, TypeCategory.Numeric, TypeCapability.None,
                ValidationKind.Numeric, all, langs, numeric, DbTypeLimits.None),

            Meta(DbDataType.Double, TypeCategory.Numeric, TypeCapability.None,
                ValidationKind.Numeric, all, langs, numeric, DbTypeLimits.None),

            Meta(DbDataType.Char, TypeCategory.Text,
                TypeCapability.Length | TypeCapability.Collatable,
                ValidationKind.String, all, langs, stringVal,
                new DbTypeLimits { DefaultLength = 1, MinLength = 1, MaxLength = 255 }),

            Meta(DbDataType.VarChar, TypeCategory.Text,
                TypeCapability.Length | TypeCapability.Collatable,
                ValidationKind.String, all, langs, stringVal,
                new DbTypeLimits { DefaultLength = 255, MinLength = 1, MaxLength = 65_535 }),

            Meta(DbDataType.Text, TypeCategory.Text,
                TypeCapability.Collatable | TypeCapability.LargeObject,
                ValidationKind.String, all, langs, stringVal, DbTypeLimits.None),

            Meta(DbDataType.Binary, TypeCategory.Binary, TypeCapability.Length,
                ValidationKind.Binary, all, langs, binaryVal,
                new DbTypeLimits { DefaultLength = 1, MinLength = 1, MaxLength = 255 }),

            Meta(DbDataType.VarBinary, TypeCategory.Binary, TypeCapability.Length,
                ValidationKind.Binary, all, langs, binaryVal,
                new DbTypeLimits { DefaultLength = 255, MinLength = 1, MaxLength = 65_535 }),

            Meta(DbDataType.Blob, TypeCategory.Binary, TypeCapability.LargeObject,
                ValidationKind.Binary, all, langs, binaryVal, DbTypeLimits.None),

            Meta(DbDataType.Date, TypeCategory.Temporal, TypeCapability.None,
                ValidationKind.Date, all, langs, dateVal, DbTypeLimits.None),

            Meta(DbDataType.Time, TypeCategory.Temporal, TypeCapability.FractionalSeconds,
                ValidationKind.Date, all, langs, dateVal,
                new DbTypeLimits { DefaultPrecision = 6, MaxPrecision = 9 }),

            Meta(DbDataType.Timestamp, TypeCategory.Temporal, TypeCapability.FractionalSeconds,
                ValidationKind.Date, all, langs, dateVal,
                new DbTypeLimits { DefaultPrecision = 6, MaxPrecision = 9 }),

            Meta(DbDataType.TimestampTz, TypeCategory.Temporal, TypeCapability.FractionalSeconds,
                ValidationKind.Date, all, langs, dateVal,
                new DbTypeLimits { DefaultPrecision = 6, MaxPrecision = 9 }),

            Meta(DbDataType.Uuid, TypeCategory.Uuid, TypeCapability.None,
                ValidationKind.None, all, langs, noVal, DbTypeLimits.None),

            Meta(DbDataType.Json, TypeCategory.Document, TypeCapability.None,
                ValidationKind.None, all, langs, noVal, DbTypeLimits.None),

            Meta(DbDataType.JsonBinary, TypeCategory.Document, TypeCapability.None,
                ValidationKind.None,
                new[] { DatabaseProvider.PostgreSql }.ToFrozenSet(),
                langs, noVal, DbTypeLimits.None),

            Meta(DbDataType.Xml, TypeCategory.Document, TypeCapability.None,
                ValidationKind.None,
                new[] { DatabaseProvider.PostgreSql, DatabaseProvider.SqlServer }.ToFrozenSet(),
                langs, noVal, DbTypeLimits.None),
        };

        return list.ToFrozenDictionary(m => m.DataType);
    }

    private static DbDataTypeMetadata Meta(
        DbDataType dataType,
        TypeCategory category,
        TypeCapability capabilities,
        ValidationKind validationKind,
        FrozenSet<DatabaseProvider> providers,
        FrozenSet<Language> languages,
        FrozenSet<ValidationKind> supportedValidations,
        DbTypeLimits limits) =>
        new()
        {
            DataType = dataType,
            Category = category,
            Capabilities = capabilities,
            ValidationKind = validationKind,
            SupportedProviders = providers,
            SupportedLanguages = languages,
            SupportedValidations = supportedValidations,
            Limits = limits,
        };
}
