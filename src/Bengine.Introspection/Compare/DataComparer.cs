namespace Bengine.Introspection.Compare;

using System.Globalization;
using Bengine.Enums;
using Bengine.Introspection.Data;
using Bengine.Introspection.Model;
using Bengine.Models;

/// <summary>How a row differs between two tables.</summary>
public enum RowDiffKind
{
    /// <summary>The key exists only in the source.</summary>
    OnlyInSource,

    /// <summary>The key exists only in the target.</summary>
    OnlyInTarget,

    /// <summary>Both have the key, with different values.</summary>
    Different,
}

/// <summary>One differing row.</summary>
/// <param name="Kind">How it differs.</param>
/// <param name="Key">The key values.</param>
/// <param name="Source">The source row (compared columns), or <c>null</c>.</param>
/// <param name="Target">The target row (compared columns), or <c>null</c>.</param>
/// <param name="Changed">The indexes of the compared columns whose values differ.</param>
public sealed record RowDiff(RowDiffKind Kind, IReadOnlyList<object?> Key, IReadOnlyList<object?>? Source, IReadOnlyList<object?>? Target, IReadOnlySet<int> Changed);

/// <summary>Options of a data comparison.</summary>
public sealed record DataCompareOptions
{
    /// <summary>Gets the key columns, or <c>null</c> for the target's (else the source's) primary key.</summary>
    public IReadOnlyList<string>? KeyColumns { get; init; }

    /// <summary>Gets columns left out of the comparison.</summary>
    public IReadOnlyCollection<string> IgnoredColumns { get; init; } = [];

    /// <summary>Gets the most differences kept (all are counted).</summary>
    public int MaxDifferences { get; init; } = 100_000;
}

/// <summary>The outcome of comparing two tables' rows.</summary>
public sealed record DataCompareResult
{
    /// <summary>Gets the compared columns (names as in the target).</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>Gets the key columns.</summary>
    public required IReadOnlyList<string> KeyColumns { get; init; }

    /// <summary>Gets the differences kept (see <see cref="Truncated"/>).</summary>
    public required IReadOnlyList<RowDiff> Differences { get; init; }

    /// <summary>Gets the rows that are equal.</summary>
    public long Same { get; init; }

    /// <summary>Gets the keys only in the source.</summary>
    public long OnlyInSource { get; init; }

    /// <summary>Gets the keys only in the target.</summary>
    public long OnlyInTarget { get; init; }

    /// <summary>Gets the rows with different values.</summary>
    public long Different { get; init; }

    /// <summary>Gets a value indicating whether more differences exist than were kept.</summary>
    public bool Truncated { get; init; }

    /// <summary>Gets columns that only one side has (not compared).</summary>
    public IReadOnlyList<string> UnmatchedColumns { get; init; } = [];

    /// <summary>Gets target columns the server fills in (identity, generated), which a sync must not write as-is.</summary>
    public IReadOnlySet<string> TargetIdentityColumns { get; init; } = new HashSet<string>();

    /// <summary>Gets a value indicating whether the tables hold the same rows.</summary>
    public bool Identical => OnlyInSource + OnlyInTarget + Different == 0;

    /// <summary>Builds the changes that make the target's rows equal to the source's.</summary>
    /// <param name="insert">Whether to insert rows only in the source.</param>
    /// <param name="update">Whether to update rows that differ.</param>
    /// <param name="delete">Whether to delete rows only in the target.</param>
    /// <returns>The changes, deletes first.</returns>
    public IReadOnlyList<RowChange> SyncChanges(bool insert = true, bool update = true, bool delete = true)
    {
        var keyIndexes = KeyColumns.Select(k => IndexOf(k)).ToList();
        Dictionary<string, object?> KeyOf(RowDiff diff) =>
            KeyColumns.Select((k, i) => (k, diff.Key[i])).ToDictionary(p => p.k, p => p.Item2, StringComparer.Ordinal);

        var changes = new List<RowChange>();
        if (delete)
        {
            changes.AddRange(Differences.Where(d => d.Kind == RowDiffKind.OnlyInTarget).Select(d => RowChange.Delete(KeyOf(d))));
        }

        if (update)
        {
            changes.AddRange(Differences
                .Where(d => d.Kind == RowDiffKind.Different && d.Changed.Any(i => !keyIndexes.Contains(i)))
                .Select(d => RowChange.Update(
                    KeyOf(d),
                    d.Changed.Where(i => !keyIndexes.Contains(i)).ToDictionary(i => Columns[i], i => d.Source![i], StringComparer.Ordinal))));
        }

        if (insert)
        {
            changes.AddRange(Differences.Where(d => d.Kind == RowDiffKind.OnlyInSource).Select(d => RowChange.Insert(
                Columns.Select((c, i) => (c, d.Source![i])).ToDictionary(p => p.c, p => p.Item2, StringComparer.Ordinal))));
        }

        return changes;
    }

    private int IndexOf(string column) => Columns.ToList().FindIndex(c => c.Equals(column, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Compares the rows of two tables by key — in the same database, across
/// databases, or across providers — and builds the INSERT/UPDATE/DELETE that
/// make the target equal to the source. The target is held in memory and the
/// source streamed against it, so ordering and collations do not matter.
/// Values compare by meaning: numbers numerically (1.50 = 1.5), dates as ISO
/// text, binary as hex, and PostgreSQL's text form against native values.
/// </summary>
public static class DataComparer
{
    /// <summary>Compares two tables.</summary>
    /// <param name="source">A session on the source database.</param>
    /// <param name="sourceTable">The source table.</param>
    /// <param name="target">A session on the target database.</param>
    /// <param name="targetTable">The target table.</param>
    /// <param name="options">The options.</param>
    /// <param name="progress">Receives progress lines.</param>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <returns>The result.</returns>
    /// <exception cref="IntrospectionException">No key is known, or a query failed.</exception>
    public static async Task<DataCompareResult> CompareAsync(
        DatabaseSession source,
        TableRef sourceTable,
        DatabaseSession target,
        TableRef targetTable,
        DataCompareOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DataCompareOptions();
        var left = await source.Introspector.IntrospectTableAsync(sourceTable, cancellationToken).ConfigureAwait(false);
        var right = await target.Introspector.IntrospectTableAsync(targetTable, cancellationToken).ConfigureAwait(false);
        var ignored = new HashSet<string>(options.IgnoredColumns, StringComparer.OrdinalIgnoreCase);
        var columns = right.Columns
            .Where(c => !ignored.Contains(c.Name) && left.Columns.Any(l => l.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Name)
            .ToList();
        var sourceNames = columns.Select(c => left.Columns.First(l => l.Name.Equals(c, StringComparison.OrdinalIgnoreCase)).Name).ToList();
        var keys = options.KeyColumns is { Count: > 0 } chosen ? chosen.ToList()
            : right.PrimaryKey?.ColumnNames.ToList() is { Count: > 0 } targetKey ? targetKey
            : left.PrimaryKey?.ColumnNames.ToList() is { Count: > 0 } sourceKey ? sourceKey
            : throw new IntrospectionException($"{targetTable.Name} has no primary key; choose the key columns to match rows by.");
        var keyIndexes = keys.Select(k => columns.FindIndex(c => c.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
        if (keyIndexes.Contains(-1))
        {
            throw new IntrospectionException($"Key column {keys[keyIndexes.IndexOf(-1)]} is not in both tables.");
        }

        keys = keyIndexes.Select(i => columns[i]).ToList();
        var unmatched = left.Columns.Select(c => c.Name).Concat(right.Columns.Select(c => c.Name))
            .Where(n => !ignored.Contains(n) && !columns.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        progress?.Report($"Reading {targetTable.Name} (target)…");
        var targetRows = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        await foreach (var row in target.Query.StreamAsync(SelectSql(target.Provider, targetTable, columns), cancellationToken).ConfigureAwait(false))
        {
            targetRows[KeyText(row, keyIndexes)] = row;
        }

        progress?.Report($"{targetRows.Count:N0} target row(s); comparing with {sourceTable.Name} (source)…");
        var differences = new List<RowDiff>();
        long same = 0, onlySource = 0, different = 0;
        void Keep(RowDiff diff)
        {
            if (differences.Count < options.MaxDifferences)
            {
                differences.Add(diff);
            }
        }

        await foreach (var row in source.Query.StreamAsync(SelectSql(source.Provider, sourceTable, sourceNames), cancellationToken).ConfigureAwait(false))
        {
            var key = KeyText(row, keyIndexes);
            if (!targetRows.Remove(key, out var other))
            {
                onlySource++;
                Keep(new RowDiff(RowDiffKind.OnlyInSource, keyIndexes.Select(i => row[i]).ToList(), row, null, new HashSet<int>()));
                continue;
            }

            var changed = new HashSet<int>();
            for (var i = 0; i < row.Length; i++)
            {
                if (!ValuesEqual(row[i], other[i]))
                {
                    changed.Add(i);
                }
            }

            if (changed.Count == 0)
            {
                same++;
            }
            else
            {
                different++;
                Keep(new RowDiff(RowDiffKind.Different, keyIndexes.Select(i => other[i]).ToList(), row, other, changed));
            }
        }

        foreach (var row in targetRows.Values)
        {
            Keep(new RowDiff(RowDiffKind.OnlyInTarget, keyIndexes.Select(i => row[i]).ToList(), null, row, new HashSet<int>()));
        }

        var result = new DataCompareResult
        {
            Columns = columns,
            KeyColumns = keys,
            Differences = differences,
            Same = same,
            OnlyInSource = onlySource,
            OnlyInTarget = targetRows.Count,
            Different = different,
            Truncated = onlySource + targetRows.Count + different > differences.Count,
            UnmatchedColumns = unmatched,
            TargetIdentityColumns = right.Columns.Where(c => c.Identity.Strategy != IdentityStrategy.None || c.Generated is not null)
                .Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
        progress?.Report($"{same:N0} equal, {different:N0} different, {onlySource:N0} only in source, {targetRows.Count:N0} only in target.");
        return result;
    }

    /// <summary>Renders the sync changes as a script for the target (with SQL Server's IDENTITY_INSERT where needed).</summary>
    /// <param name="provider">The target's provider.</param>
    /// <param name="table">The target table.</param>
    /// <param name="result">The comparison.</param>
    /// <param name="changes">The changes (from <see cref="DataCompareResult.SyncChanges"/>).</param>
    /// <returns>The script.</returns>
    public static string SyncScript(DatabaseProvider provider, TableRef table, DataCompareResult result, IReadOnlyList<RowChange> changes)
    {
        var statements = RelationalDataWriter.Preview(provider, table, changes).ToList();
        if (NeedsIdentityInsert(provider, result, changes))
        {
            var name = Qualified(provider, table);
            statements.Insert(0, $"SET IDENTITY_INSERT {name} ON;");
            statements.Add($"SET IDENTITY_INSERT {name} OFF;");
        }

        return string.Join('\n', statements) + (statements.Count > 0 ? "\n" : string.Empty);
    }

    /// <summary>Applies sync changes to the target in one transaction.</summary>
    /// <param name="target">A session on the target database.</param>
    /// <param name="table">The target table.</param>
    /// <param name="result">The comparison.</param>
    /// <param name="changes">The changes.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>The number of changes applied.</returns>
    public static async Task<int> ApplyAsync(DatabaseSession target, TableRef table, DataCompareResult result, IReadOnlyList<RowChange> changes, CancellationToken cancellationToken = default)
    {
        var identity = NeedsIdentityInsert(target.Provider, result, changes);
        var name = Qualified(target.Provider, table);
        if (identity)
        {
            await target.Query.ExecuteAllAsync($"SET IDENTITY_INSERT {name} ON", 1, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return (await target.Writer.ApplyAsync(table, changes, cancellationToken).ConfigureAwait(false)).Count;
        }
        finally
        {
            if (identity)
            {
                await target.Query.ExecuteAllAsync($"SET IDENTITY_INSERT {name} OFF", 1, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Tells whether two values mean the same, across drivers and text forms.</summary>
    /// <param name="a">One value.</param>
    /// <param name="b">The other.</param>
    /// <returns><c>true</c> when equal.</returns>
    public static bool ValuesEqual(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (Number(a) is { } x && Number(b) is { } y && (IsNumeric(a) || IsNumeric(b)))
        {
            return x == y;
        }

        if (a is bool || b is bool)
        {
            return Flag(a) is { } p && Flag(b) is { } q && p == q;
        }

        return Canonical(a) == Canonical(b);
    }

    private static string SelectSql(DatabaseProvider provider, TableRef table, IEnumerable<string> columns)
    {
        var dialect = DialectFactory.For(provider);
        return $"SELECT {string.Join(", ", columns.Select(dialect.QuoteIdentifier))} FROM {Qualified(provider, table)}";
    }

    private static string Qualified(DatabaseProvider provider, TableRef table)
    {
        var dialect = DialectFactory.For(provider);
        return table.Schema is { Length: > 0 } schema ? $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(table.Name)}" : dialect.QuoteIdentifier(table.Name);
    }

    private static bool NeedsIdentityInsert(DatabaseProvider provider, DataCompareResult result, IReadOnlyList<RowChange> changes) =>
        provider == DatabaseProvider.SqlServer
        && changes.Any(c => c.Kind == RowChangeKind.Insert && c.Values.Keys.Any(result.TargetIdentityColumns.Contains));

    private static string KeyText(object?[] row, IReadOnlyList<int> keyIndexes) =>
        string.Join('\u001f', keyIndexes.Select(i => Number(row[i]) is { } n ? n.ToString(CultureInfo.InvariantCulture) : Canonical(row[i])));

    private static bool IsNumeric(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static decimal? Number(object? value) => value switch
    {
        null or bool => null,
        float f when float.IsFinite(f) => (decimal)f,
        double d when double.IsFinite(d) => (decimal)d,
        float or double => null,
        _ when IsNumeric(value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static bool? Flag(object value) => value switch
    {
        bool b => b,
        string s when s is "t" or "true" or "1" or "TRUE" => true,
        string s when s is "f" or "false" or "0" or "FALSE" => false,
        _ when IsNumeric(value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0,
        _ => null,
    };

    private static string Canonical(object? value) => value switch
    {
        null => "\0",
        byte[] bytes => "\\x" + Convert.ToHexString(bytes).ToLowerInvariant(),
        DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + "Z",
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        Guid guid => guid.ToString(),
        string s => s.StartsWith("\\x", StringComparison.Ordinal) ? s.ToLowerInvariant() : NormalizeTimestampText(s),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    // PostgreSQL prints "2026-10-14 00:00:00" for a midnight timestamp; native drivers give a DateTime.
    private static string NormalizeTimestampText(string text) =>
        text.Length == 19 && text.EndsWith(" 00:00:00", StringComparison.Ordinal) && DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? text[..10]
            : text;
}
