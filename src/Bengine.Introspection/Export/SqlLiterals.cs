namespace Bengine.Introspection.Export;

using Bengine.Enums;
using Bengine.Introspection.Model;

/// <summary>
/// Renders CLR values as SQL literals for generated scripts (export, change
/// previews, "copy as SQL"): <c>NULL</c>, numbers as-is, booleans, binary in the
/// provider's hex form, and everything else as a quoted string with embedded
/// quotes doubled. Live commands never use this — they bind parameters.
/// </summary>
public static class SqlLiterals
{
    /// <summary>Formats a value as a literal for the provider.</summary>
    /// <param name="value">The value.</param>
    /// <param name="provider">The provider.</param>
    /// <returns>The literal.</returns>
    public static string Format(object? value, DatabaseProvider provider) => value switch
    {
        null => "NULL",
        SqlDefault => "DEFAULT",
        bool b => provider is DatabaseProvider.SqlServer or DatabaseProvider.Sqlite ? (b ? "1" : "0") : (b ? "TRUE" : "FALSE"),
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => DataExporter.Scalar(value),
        DateTime dt => Quote(dt.ToString(dt.TimeOfDay == TimeSpan.Zero && dt.Kind != DateTimeKind.Utc ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", Invariant), provider),
        DateTimeOffset dto => Quote(dto.ToString(provider == DatabaseProvider.MySql ? "yyyy-MM-dd HH:mm:ss.FFFFFF" : "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", Invariant), provider),
        DateOnly date => Quote(date.ToString("yyyy-MM-dd", Invariant), provider),
        TimeOnly time => Quote(time.ToString("HH:mm:ss.FFFFFFF", Invariant), provider),
        TimeSpan span => Quote(span.ToString(span < TimeSpan.Zero || span.Days != 0 ? "c" : @"hh\:mm\:ss\.FFFFFFF", Invariant).TrimEnd('.'), provider),
        Guid guid => Quote(guid.ToString(), provider),
        byte[] bytes => provider switch
        {
            DatabaseProvider.PostgreSql => $"'\\x{Convert.ToHexString(bytes)}'",
            DatabaseProvider.SqlServer => $"0x{Convert.ToHexString(bytes)}",
            _ => $"X'{Convert.ToHexString(bytes)}'",
        },
        _ => Quote(DataExporter.Scalar(value), provider),
    };

    private static System.Globalization.CultureInfo Invariant => System.Globalization.CultureInfo.InvariantCulture;

    private static string Quote(string text, DatabaseProvider provider) =>
        (provider == DatabaseProvider.SqlServer ? "N'" : "'")
        + text.Replace("'", "''", StringComparison.Ordinal)
            .Replace("\\", provider == DatabaseProvider.MySql ? "\\\\" : "\\", StringComparison.Ordinal) + "'";
}
