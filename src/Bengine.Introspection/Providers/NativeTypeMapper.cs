namespace Bengine.Introspection.Providers;

using Bengine.Enums;
using Bengine.Models.Options;
using Bengine.Parsing;

/// <summary>
/// Turns a catalog's native type description into a logical
/// <see cref="ColumnTypeOptions"/>. It defers to the parser's
/// <see cref="SqlTypeResolver"/> so introspection and DDL import agree on type
/// spellings, but — unlike import, which rejects unknown types — it falls back
/// to <see cref="DbDataType.Text"/> for a spelling no provider map recognizes,
/// so a single exotic column never fails a whole-database read.
/// </summary>
internal static class NativeTypeMapper
{
    /// <summary>Maps a native type to logical type options.</summary>
    /// <param name="provider">The source provider.</param>
    /// <param name="nativeName">The native base type name (without length/precision).</param>
    /// <param name="length">The character or byte length, if the catalog reported one.</param>
    /// <param name="precision">The numeric precision, if applicable.</param>
    /// <param name="scale">The numeric scale, if applicable.</param>
    /// <param name="isUnsigned">Whether the numeric type is unsigned.</param>
    /// <returns>The logical type options, and whether the spelling implies auto-increment.</returns>
    public static (ColumnTypeOptions Type, bool ImpliesAutoIncrement) Map(
        DatabaseProvider provider,
        string nativeName,
        int? length = null,
        int? precision = null,
        int? scale = null,
        bool isUnsigned = false)
    {
        DbDataType dataType;
        var impliesAutoIncrement = false;
        try
        {
            var resolved = SqlTypeResolver.Resolve(provider, nativeName, length ?? precision);
            dataType = resolved.DataType;
            impliesAutoIncrement = resolved.ImpliesAutoIncrement;
        }
        catch (SqlParseException)
        {
            dataType = DbDataType.Text;
        }

        var options = new ColumnTypeOptions
        {
            DataType = dataType,
            Length = CarriesLength(dataType) ? length : null,
            Precision = CarriesPrecision(dataType) ? precision : null,
            Scale = CarriesPrecision(dataType) ? scale : null,
            IsUnsigned = isUnsigned,
        };

        return (options, impliesAutoIncrement);
    }

    private static bool CarriesLength(DbDataType type) => type
        is DbDataType.Char or DbDataType.VarChar
        or DbDataType.Binary or DbDataType.VarBinary;

    private static bool CarriesPrecision(DbDataType type) => type is DbDataType.Decimal;
}
