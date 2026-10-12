namespace Bengine.Api;

using System.Collections;
using System.Globalization;
using System.Text;

/// <summary>
/// Normalizes the raw CLR values a driver returns into JSON-safe cells for the
/// browser. JavaScript numbers are doubles, so integers beyond ±2^53 and all
/// decimals are sent as exact strings; non-finite doubles (which JSON cannot
/// encode), binary data, arrays, and provider-specific types (network
/// addresses, ranges, geometric types) are rendered as text. Primitives that
/// round-trip exactly pass through unchanged.
/// </summary>
public static class CellFormatter
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private const int MaxBinaryBytes = 64;

    /// <summary>Normalizes every cell of a block of rows.</summary>
    /// <param name="rows">The rows as the driver returned them.</param>
    /// <param name="types">The database type name of each column, used to tell a <c>date</c> from a midnight <c>timestamp</c>.</param>
    /// <returns>The JSON-safe rows.</returns>
    public static IReadOnlyList<object?[]> Rows(IReadOnlyList<IReadOnlyList<object?>> rows, IReadOnlyList<string> types) =>
        rows.Select(row => row.Select((value, i) => IsDateType(types, i) && value is DateTime date
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : Cell(value)).ToArray()).ToList();

    private static bool IsDateType(IReadOnlyList<string> types, int index) =>
        index < types.Count && types[index].Equals("date", StringComparison.OrdinalIgnoreCase);

    /// <summary>Normalizes one cell.</summary>
    /// <param name="value">The raw value.</param>
    /// <returns>A JSON-safe value.</returns>
    public static object? Cell(object? value) => value switch
    {
        null or DBNull => null,
        string or bool or byte or sbyte or short or ushort or int or uint => value,
        long l => l is >= -MaxSafeInteger and <= MaxSafeInteger ? l : l.ToString(CultureInfo.InvariantCulture),
        ulong u => u <= MaxSafeInteger ? u : u.ToString(CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d => double.IsFinite(d) ? d : d.ToString(CultureInfo.InvariantCulture),
        float f => float.IsFinite(f) ? f : f.ToString(CultureInfo.InvariantCulture),
        DateTime { Kind: DateTimeKind.Utc } utc => utc.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture) + "+00",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        Guid guid => guid.ToString(),
        byte[] bytes => Binary(bytes),
        IDictionary dictionary => string.Join(", ", dictionary.Keys.Cast<object>().Select(k => $"{k}=>{dictionary[k]}")),
        Array array => ArrayLiteral(array),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string Binary(byte[] bytes) =>
        bytes.Length <= MaxBinaryBytes
            ? "\\x" + Convert.ToHexString(bytes).ToLowerInvariant()
            : $"\\x{Convert.ToHexString(bytes, 0, MaxBinaryBytes).ToLowerInvariant()}… ({bytes.Length:N0} bytes)";

    // PostgreSQL-style array literal: {a,b,"c d"}.
    private static string ArrayLiteral(Array array)
    {
        var builder = new StringBuilder("{");
        var first = true;
        foreach (var item in array)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            var text = Cell(item) switch
            {
                null => "NULL",
                string s when s.Length == 0 || s.Any(c => char.IsWhiteSpace(c) || c is ',' or '"' or '{' or '}') => "\"" + s.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
                var other => Convert.ToString(other, CultureInfo.InvariantCulture),
            };
            builder.Append(text);
        }

        return builder.Append('}').ToString();
    }
}
