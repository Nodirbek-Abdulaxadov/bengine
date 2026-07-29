namespace Bengine.Parsing;

using System.Collections.Frozen;
using System.Collections.Generic;
using Bengine.Enums;
using Bengine.Mapping;

/// <summary>
/// Resolves a native SQL type spelling into the framework's logical
/// <see cref="DbDataType"/>. A broad alias table recognizes the many native names
/// the four providers use for the same logical type; where a provider spelling is
/// canonical it defers to <see cref="ProviderTypeMap"/> so the mapping layer
/// remains the source of truth. This is the inverse counterpart of
/// <see cref="ITypeMapper.RenderProviderType"/> used during import.
/// </summary>
public static class SqlTypeResolver
{
    /// <summary>The outcome of resolving a native type name.</summary>
    /// <param name="DataType">The logical type the native name maps to.</param>
    /// <param name="ImpliesAutoIncrement">Whether the native spelling implies auto-increment (for example <c>serial</c>).</param>
    public readonly record struct Resolved(DbDataType DataType, bool ImpliesAutoIncrement);

    // Auto-incrementing pseudo-types that also fix the underlying integer width.
    private static readonly FrozenDictionary<string, DbDataType> SerialTypes =
        new Dictionary<string, DbDataType>(StringComparer.OrdinalIgnoreCase)
        {
            ["serial"] = DbDataType.Integer,
            ["serial4"] = DbDataType.Integer,
            ["bigserial"] = DbDataType.BigInt,
            ["serial8"] = DbDataType.BigInt,
            ["smallserial"] = DbDataType.SmallInt,
            ["serial2"] = DbDataType.SmallInt,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Native spellings across all four providers → logical type.
    private static readonly FrozenDictionary<string, DbDataType> Aliases =
        new Dictionary<string, DbDataType>(StringComparer.OrdinalIgnoreCase)
        {
            ["bool"] = DbDataType.Boolean,
            ["boolean"] = DbDataType.Boolean,
            ["bit"] = DbDataType.Boolean,

            ["tinyint"] = DbDataType.TinyInt,
            ["int1"] = DbDataType.TinyInt,

            ["smallint"] = DbDataType.SmallInt,
            ["int2"] = DbDataType.SmallInt,

            ["int"] = DbDataType.Integer,
            ["integer"] = DbDataType.Integer,
            ["int4"] = DbDataType.Integer,
            ["mediumint"] = DbDataType.Integer,

            ["bigint"] = DbDataType.BigInt,
            ["int8"] = DbDataType.BigInt,

            ["decimal"] = DbDataType.Decimal,
            ["numeric"] = DbDataType.Decimal,
            ["number"] = DbDataType.Decimal,
            ["dec"] = DbDataType.Decimal,
            ["fixed"] = DbDataType.Decimal,
            ["money"] = DbDataType.Decimal,
            ["smallmoney"] = DbDataType.Decimal,

            ["real"] = DbDataType.Real,
            ["float4"] = DbDataType.Real,

            ["double"] = DbDataType.Double,
            ["double precision"] = DbDataType.Double,
            ["float"] = DbDataType.Double,
            ["float8"] = DbDataType.Double,

            ["char"] = DbDataType.Char,
            ["character"] = DbDataType.Char,
            ["nchar"] = DbDataType.Char,
            ["bpchar"] = DbDataType.Char,

            ["varchar"] = DbDataType.VarChar,
            ["character varying"] = DbDataType.VarChar,
            ["nvarchar"] = DbDataType.VarChar,
            ["varchar2"] = DbDataType.VarChar,
            ["nvarchar2"] = DbDataType.VarChar,
            ["varying"] = DbDataType.VarChar,

            ["text"] = DbDataType.Text,
            ["ntext"] = DbDataType.Text,
            ["clob"] = DbDataType.Text,
            ["tinytext"] = DbDataType.Text,
            ["mediumtext"] = DbDataType.Text,
            ["longtext"] = DbDataType.Text,
            ["citext"] = DbDataType.Text,

            ["binary"] = DbDataType.Binary,

            ["varbinary"] = DbDataType.VarBinary,

            ["blob"] = DbDataType.Blob,
            ["tinyblob"] = DbDataType.Blob,
            ["mediumblob"] = DbDataType.Blob,
            ["longblob"] = DbDataType.Blob,
            ["bytea"] = DbDataType.Blob,
            ["image"] = DbDataType.Blob,

            ["date"] = DbDataType.Date,

            ["time"] = DbDataType.Time,

            ["timestamp"] = DbDataType.Timestamp,
            ["datetime"] = DbDataType.Timestamp,
            ["datetime2"] = DbDataType.Timestamp,
            ["smalldatetime"] = DbDataType.Timestamp,

            ["timestamptz"] = DbDataType.TimestampTz,
            ["datetimeoffset"] = DbDataType.TimestampTz,

            ["uuid"] = DbDataType.Uuid,
            ["uniqueidentifier"] = DbDataType.Uuid,

            ["json"] = DbDataType.Json,
            ["jsonb"] = DbDataType.JsonBinary,
            ["xml"] = DbDataType.Xml,

            ["year"] = DbDataType.SmallInt,

            // MySQL inline enumerations (declared as enum('a','b') / set('a','b')
            // directly on the column) have no logical equivalent → Text.
            ["enum"] = DbDataType.Text,
            ["set"] = DbDataType.Text,

            // Provider-specific and extension types with no dedicated logical
            // equivalent are represented as Text (their canonical text form).
            ["inet"] = DbDataType.Text,
            ["cidr"] = DbDataType.Text,
            ["macaddr"] = DbDataType.Text,
            ["macaddr8"] = DbDataType.Text,
            ["interval"] = DbDataType.Text,
            ["tsvector"] = DbDataType.Text,
            ["tsquery"] = DbDataType.Text,
            ["hstore"] = DbDataType.Text,
            ["ltree"] = DbDataType.Text,
            ["varbit"] = DbDataType.Text,
            ["bit varying"] = DbDataType.Text,
            ["geometry"] = DbDataType.Text,
            ["geography"] = DbDataType.Text,
            ["point"] = DbDataType.Text,
            ["line"] = DbDataType.Text,
            ["lseg"] = DbDataType.Text,
            ["box"] = DbDataType.Text,
            ["path"] = DbDataType.Text,
            ["polygon"] = DbDataType.Text,
            ["circle"] = DbDataType.Text,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a native type name for a provider.</summary>
    /// <param name="provider">The provider the SQL originates from, used for provider-specific spellings.</param>
    /// <param name="nativeName">The native base type name (multi-word names normalized to single spaces).</param>
    /// <param name="firstArgument">The first parenthesized argument, if any (for spellings like <c>tinyint(1)</c>).</param>
    /// <returns>The resolved logical type.</returns>
    /// <exception cref="SqlParseException">The native name is not recognized.</exception>
    public static Resolved Resolve(DatabaseProvider provider, string nativeName, int? firstArgument)
    {
        var name = Normalize(nativeName);

        if (SerialTypes.TryGetValue(name, out var serial))
        {
            return new Resolved(serial, ImpliesAutoIncrement: true);
        }

        // MySQL renders Boolean as tinyint(1).
        if (provider == DatabaseProvider.MySql && name is "tinyint" && firstArgument == 1)
        {
            return new Resolved(DbDataType.Boolean, ImpliesAutoIncrement: false);
        }

        if (Aliases.TryGetValue(name, out var dataType))
        {
            return new Resolved(dataType, ImpliesAutoIncrement: false);
        }

        // Fall back to the mapping layer's curated reverse map.
        if (ProviderTypeMap.TryFromNative(provider, name, out var mapped))
        {
            return new Resolved(mapped, ImpliesAutoIncrement: false);
        }

        throw new SqlParseException($"Unrecognized SQL type '{nativeName}'.");
    }

    private static string Normalize(string nativeName) =>
        string.Join(' ', nativeName.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
}
