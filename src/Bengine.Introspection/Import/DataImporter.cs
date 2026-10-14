namespace Bengine.Introspection.Import;

using System.Globalization;
using System.Text;
using Bengine.Enums;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Design;
using Bengine.Introspection.Export;
using Bengine.Introspection.Model;

/// <summary>How a delimited file is read.</summary>
public sealed record ImportOptions
{
    /// <summary>Gets the field delimiter, or <c>null</c> to detect it.</summary>
    public char? Delimiter { get; init; }

    /// <summary>Gets whether the first record holds the column names.</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>Gets whether empty fields are imported as <c>NULL</c> (otherwise as empty strings).</summary>
    public bool EmptyIsNull { get; init; } = true;

    /// <summary>Gets whether fields are trimmed.</summary>
    public bool Trim { get; init; } = true;
}

/// <summary>The first records of a file and the column types inferred from them.</summary>
/// <param name="Delimiter">The delimiter used.</param>
/// <param name="Headers">The column names (from the header, or <c>column1</c>…).</param>
/// <param name="Rows">The sampled records.</param>
/// <param name="Types">The inferred type of each column, in the target provider's spelling.</param>
public sealed record ImportPreview(char Delimiter, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<string> Types);

/// <summary>Where one file column goes: a table column, or nowhere.</summary>
/// <param name="FileColumn">The column's position in the file.</param>
/// <param name="TableColumn">The target column, or <c>null</c> to skip it.</param>
public sealed record ColumnMapping(int FileColumn, string? TableColumn);

/// <summary>
/// Imports CSV/TSV (or any delimited) files into a table — an existing one with
/// a column mapping, or a new one designed from the file with inferred types.
/// Rows are inserted with multi-row parameterized <c>INSERT</c>s in batches
/// sized to the provider's parameter limit, all inside one transaction, so a
/// failed import leaves the table as it was.
/// </summary>
public static class DataImporter
{
    /// <summary>Reads the head of a file and infers its columns.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">The read options.</param>
    /// <param name="provider">The target provider (for type spellings).</param>
    /// <param name="sample">How many records to sample.</param>
    /// <returns>The preview.</returns>
    public static ImportPreview Preview(string path, ImportOptions options, DatabaseProvider provider, int sample = 200)
    {
        var delimiter = options.Delimiter ?? DetectDelimiter(path);
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var records = CsvText.Read(reader, delimiter).Take(sample + 1).ToList();
        var width = records.Count == 0 ? 0 : records.Max(r => r.Count);
        var headers = options.HasHeader && records.Count > 0
            ? Enumerable.Range(0, width).Select(i => i < records[0].Count && records[0][i].Trim().Length > 0 ? records[0][i].Trim() : $"column{i + 1}").ToList()
            : Enumerable.Range(1, width).Select(i => $"column{i}").ToList();
        var rows = (options.HasHeader ? records.Skip(1) : records).Take(sample).Select(r => (IReadOnlyList<string>)r.Select(f => options.Trim ? f.Trim() : f).ToList()).ToList();
        var types = Enumerable.Range(0, width).Select(i => Infer(rows.Select(r => i < r.Count ? r[i] : string.Empty), provider)).ToList();
        return new ImportPreview(delimiter, Unique(headers), rows, types);
    }

    /// <summary>Designs a new table for a file: one nullable column per file column, with inferred types.</summary>
    /// <param name="preview">The file preview.</param>
    /// <param name="provider">The provider.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="name">The table name.</param>
    /// <returns>The design.</returns>
    public static TableDesign DesignTable(ImportPreview preview, DatabaseProvider provider, string? schema, string name)
    {
        var design = new TableDesign(provider, schema) { Name = name };
        for (var i = 0; i < preview.Headers.Count; i++)
        {
            design.Columns.Add(new ColumnDesign { Name = ColumnName(preview.Headers[i]), Type = preview.Types[i], Nullable = true });
        }

        return design;
    }

    /// <summary>Imports a file into a table.</summary>
    /// <param name="session">The session.</param>
    /// <param name="table">The target table.</param>
    /// <param name="path">The file.</param>
    /// <param name="options">The read options (with the delimiter resolved).</param>
    /// <param name="mapping">Where each file column goes.</param>
    /// <param name="columnTypes">The target columns' types, by name, to convert booleans where the column is numeric.</param>
    /// <param name="progress">Receives the number of rows imported so far.</param>
    /// <param name="cancellationToken">Cancels the import (nothing is kept).</param>
    /// <returns>The number of rows imported.</returns>
    /// <exception cref="IntrospectionException">A row was refused; nothing was imported.</exception>
    public static async Task<long> ImportAsync(
        DatabaseSession session,
        TableRef table,
        string path,
        ImportOptions options,
        IReadOnlyList<ColumnMapping> mapping,
        IReadOnlyDictionary<string, string>? columnTypes = null,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var used = mapping.Where(m => m.TableColumn is not null).ToList();
        if (used.Count == 0)
        {
            throw new IntrospectionException("Map at least one file column to a table column.");
        }

        var delimiter = options.Delimiter ?? DetectDelimiter(path);
        var limit = session.Provider switch
        {
            DatabaseProvider.SqlServer => 2000,
            DatabaseProvider.Sqlite => 999,

            // Oracle parses one INSERT ALL per batch; keep the statement modest.
            DatabaseProvider.Oracle => 2000,
            _ => 30000,
        };
        var batchRows = Math.Clamp(limit / used.Count, 1, 1000);
        var numeric = used.Select(m => columnTypes?.GetValueOrDefault(m.TableColumn!) is { } type
            && (type.Contains("int", StringComparison.OrdinalIgnoreCase) || type.Equals("bit", StringComparison.OrdinalIgnoreCase))).ToList();

        var ownsTransaction = !session.InTransaction;
        if (ownsTransaction)
        {
            await session.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        long count = 0;
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var batch = new List<IReadOnlyDictionary<string, object?>>(batchRows);
            var first = true;
            foreach (var record in CsvText.Read(reader, delimiter))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (first && options.HasHeader)
                {
                    first = false;
                    continue;
                }

                first = false;
                var values = new Dictionary<string, object?>(used.Count);
                for (var i = 0; i < used.Count; i++)
                {
                    var raw = used[i].FileColumn < record.Count ? record[used[i].FileColumn] : string.Empty;
                    var text = options.Trim ? raw.Trim() : raw;
                    values[used[i].TableColumn!] = text.Length == 0 && options.EmptyIsNull ? null
                        : numeric[i] && Bool(text) is { } flag ? (flag ? 1 : 0)
                        : text;
                }

                batch.Add(values);
                if (batch.Count == batchRows)
                {
                    count += await InsertBatchAsync(session, table, batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                    progress?.Report(count);
                }
            }

            if (batch.Count > 0)
            {
                count += await InsertBatchAsync(session, table, batch, cancellationToken).ConfigureAwait(false);
                progress?.Report(count);
            }

            if (ownsTransaction)
            {
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return count;
        }
        catch
        {
            if (ownsTransaction && session.InTransaction)
            {
                await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Guesses a file's delimiter from its first line.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The delimiter.</returns>
    public static char DetectDelimiter(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var line = reader.ReadLine() ?? string.Empty;
        return path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tab", StringComparison.OrdinalIgnoreCase)
            ? '\t'
            : CsvText.DetectDelimiter(line);
    }

    /// <summary>Turns a header into a column name: trimmed, spaces to underscores, lower-case where the provider folds case.</summary>
    /// <param name="header">The header.</param>
    /// <returns>The name.</returns>
    public static string ColumnName(string header)
    {
        var name = string.Concat(header.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_');
        name = name.Length == 0 ? "column" : name;
        return char.IsDigit(name[0]) ? "c_" + name : name.ToLowerInvariant();
    }

    private static async Task<long> InsertBatchAsync(DatabaseSession session, TableRef table, List<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken)
    {
        var counts = await session.Writer.InsertManyAsync(table, rows, cancellationToken).ConfigureAwait(false);
        return counts;
    }

    // The narrowest type that fits every sampled value.
    private static string Infer(IEnumerable<string> values, DatabaseProvider provider)
    {
        var present = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        if (present.Count == 0)
        {
            return Spell(provider, "text", 0);
        }

        if (present.All(v => long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) && !(v.Length > 1 && v.TrimStart('-')[0] == '0')))
        {
            return Spell(provider, present.All(v => int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) ? "int" : "bigint", 0);
        }

        if (present.All(v => decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return Spell(provider, "decimal", 0);
        }

        if (present.All(v => Bool(v) is not null))
        {
            return Spell(provider, "bool", 0);
        }

        if (present.All(v => DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
        {
            return Spell(provider, "date", 0);
        }

        if (present.All(v => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _) && v.Length >= 10 && v[4] == '-'))
        {
            return Spell(provider, "timestamp", 0);
        }

        return Spell(provider, "text", present.Max(v => v.Length));
    }

    private static string Spell(DatabaseProvider provider, string type, int length) => (provider, type) switch
    {
        (DatabaseProvider.Sqlite, "int" or "bigint" or "bool") => "INTEGER",
        (DatabaseProvider.Sqlite, "decimal") => "REAL",
        (DatabaseProvider.Sqlite, _) => "TEXT",
        (DatabaseProvider.PostgreSql, "int") => "integer",
        (DatabaseProvider.PostgreSql, "decimal") => "numeric",
        (DatabaseProvider.PostgreSql, "bool") => "boolean",
        (DatabaseProvider.PostgreSql, "text") => "text",
        (DatabaseProvider.MySql, "bool") => "tinyint(1)",
        (DatabaseProvider.MySql, "decimal") => "decimal(30,10)",
        (DatabaseProvider.MySql, "timestamp") => "datetime",
        (DatabaseProvider.MySql, "text") => length is > 0 and <= 255 ? "varchar(255)" : "text",
        (DatabaseProvider.SqlServer, "bool") => "bit",
        (DatabaseProvider.SqlServer, "decimal") => "decimal(30,10)",
        (DatabaseProvider.SqlServer, "timestamp") => "datetime2",
        (DatabaseProvider.SqlServer, "text") => length is > 0 and <= 4000 ? "nvarchar(4000)" : "nvarchar(max)",
        (DatabaseProvider.Oracle, "int" or "bigint") => "NUMBER(19)",
        (DatabaseProvider.Oracle, "decimal") => "NUMBER",
        (DatabaseProvider.Oracle, "bool") => "NUMBER(1)",
        (DatabaseProvider.Oracle, "timestamp") => "TIMESTAMP",
        (DatabaseProvider.Oracle, "date") => "DATE",
        (DatabaseProvider.Oracle, "text") => length is > 0 and <= 255 ? "VARCHAR2(255 CHAR)" : length is > 0 and <= 4000 ? "VARCHAR2(4000 CHAR)" : "CLOB",
        _ => type,
    };

    private static bool? Bool(string value) => value.ToLowerInvariant() switch
    {
        "true" or "yes" or "y" or "t" => true,
        "false" or "no" or "n" or "f" => false,
        _ => null,
    };

    private static List<string> Unique(List<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return names.Select(n =>
        {
            var name = n;
            for (var i = 2; !seen.Add(name); i++)
            {
                name = $"{n}_{i}";
            }

            return name;
        }).ToList();
    }
}
