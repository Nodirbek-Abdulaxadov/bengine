namespace Bengine.Introspection.Export;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Bengine.Enums;
using Bengine.Generators.Sql;
using Bengine.Introspection.Data;
using Bengine.Introspection.Model;

/// <summary>
/// Formats a block of result rows for export as CSV, JSON, or a batch of
/// <c>INSERT</c> statements. Export renders values as literals (it produces a
/// file, not a live command), so it escapes CSV fields and SQL string literals
/// itself; identifiers in the SQL form are quoted with the provider's dialect.
/// </summary>
public static class DataExporter
{
    /// <summary>Renders rows as RFC 4180 CSV with a header row.</summary>
    /// <param name="columns">The column names.</param>
    /// <param name="rows">The rows, aligned with <paramref name="columns"/>.</param>
    /// <returns>The CSV text.</returns>
    public static string ToCsv(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", columns.Select(CsvField)));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", row.Select(cell => CsvField(Scalar(cell)))));
        }

        return builder.ToString();
    }

    /// <summary>Renders rows as tab-separated text with a header row, as spreadsheets paste it.</summary>
    /// <param name="columns">The column names.</param>
    /// <param name="rows">The rows, aligned with <paramref name="columns"/>.</param>
    /// <returns>The TSV text.</returns>
    public static string ToTsv(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        static string Field(string value) =>
            value.Contains('\t') || value.Contains('\n') || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

        var builder = new StringBuilder();
        builder.AppendLine(string.Join("\t", columns.Select(Field)));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join("\t", row.Select(cell => cell is null ? string.Empty : Field(Scalar(cell)))));
        }

        return builder.ToString();
    }

    /// <summary>Renders rows as a Markdown table.</summary>
    /// <param name="columns">The column names.</param>
    /// <param name="rows">The rows, aligned with <paramref name="columns"/>.</param>
    /// <returns>The Markdown text.</returns>
    public static string ToMarkdown(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);

        var builder = new StringBuilder();
        builder.Append("| ").AppendJoin(" | ", columns.Select(Cell)).AppendLine(" |");
        builder.Append('|').AppendJoin("|", columns.Select(_ => " --- ")).AppendLine("|");
        foreach (var row in rows)
        {
            builder.Append("| ").AppendJoin(" | ", row.Select(cell => cell is null ? "NULL" : Cell(Scalar(cell)))).AppendLine(" |");
        }

        return builder.ToString();
    }

    /// <summary>Renders rows as a JSON array of objects keyed by column name.</summary>
    /// <param name="columns">The column names.</param>
    /// <param name="rows">The rows, aligned with <paramref name="columns"/>.</param>
    /// <returns>The JSON text.</returns>
    public static string ToJson(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                for (var i = 0; i < columns.Count && i < row.Count; i++)
                {
                    writer.WritePropertyName(columns[i]);
                    WriteJsonValue(writer, row[i]);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Renders rows as a batch of <c>INSERT</c> statements for the provider.</summary>
    /// <param name="provider">The dialect whose identifier quoting to use.</param>
    /// <param name="table">The target relation.</param>
    /// <param name="columns">The column names.</param>
    /// <param name="rows">The rows, aligned with <paramref name="columns"/>.</param>
    /// <returns>The INSERT script.</returns>
    public static string ToSqlInserts(
        DatabaseProvider provider,
        TableRef table,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var dialect = DialectFactory.For(provider);
        var qualified = table.Schema is { Length: > 0 } schema
            ? $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(table.Name)}"
            : dialect.QuoteIdentifier(table.Name);
        var columnList = string.Join(", ", columns.Select(dialect.QuoteIdentifier));

        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            var values = string.Join(", ", row.Select(cell => SqlLiterals.Format(cell, provider)));
            builder.Append("INSERT INTO ").Append(qualified)
                .Append(" (").Append(columnList).Append(") VALUES (")
                .Append(values).AppendLine(");");
        }

        return builder.ToString();
    }

    private static string CsvField(string value)
    {
        var needsQuote = value.Contains(',') || value.Contains('"')
            || value.Contains('\n') || value.Contains('\r');
        if (!needsQuote)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    internal static string Scalar(object? value) => value switch
    {
        null => string.Empty,
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case float or double or decimal:
                writer.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                break;
            case byte[] bytes:
                writer.WriteStringValue("0x" + Convert.ToHexString(bytes));
                break;
            default:
                writer.WriteStringValue(Scalar(value));
                break;
        }
    }

}
