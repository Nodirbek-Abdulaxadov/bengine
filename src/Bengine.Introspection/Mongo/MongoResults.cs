namespace Bengine.Introspection.Mongo;

using Bengine.Introspection.Model;
using MongoDB.Bson;

/// <summary>
/// Lays documents out as the grid shows them: one column per top-level field
/// (in order of first appearance, <c>_id</c> first), one row per document, a
/// missing field as <c>NULL</c>, and each column typed by the BSON type its
/// values share (<c>mixed</c> when they differ).
/// </summary>
public static class MongoResults
{
    /// <summary>Flattens documents into columns, column types, and rows.</summary>
    /// <param name="documents">The documents.</param>
    /// <returns>The columns, their types, and the rows.</returns>
    public static (IReadOnlyList<string> Columns, IReadOnlyList<string> Types, IReadOnlyList<IReadOnlyList<object?>> Rows) Flatten(IReadOnlyList<BsonDocument> documents)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var columns = new List<string>();
        if (documents.Any(d => d.Contains("_id")))
        {
            index["_id"] = 0;
            columns.Add("_id");
        }

        foreach (var document in documents)
        {
            foreach (var element in document)
            {
                if (index.TryAdd(element.Name, columns.Count))
                {
                    columns.Add(element.Name);
                }
            }
        }

        var types = new string?[columns.Count];
        var rows = new List<IReadOnlyList<object?>>(documents.Count);
        foreach (var document in documents)
        {
            var row = new object?[columns.Count];
            foreach (var element in document)
            {
                var i = index[element.Name];
                row[i] = MongoValues.ToCell(element.Value);
                if (element.Value.IsBsonNull)
                {
                    continue;
                }

                var type = MongoValues.TypeName(element.Value);
                types[i] = types[i] is null || types[i] == type ? type : "mixed";
            }

            rows.Add(row);
        }

        return (columns, types.Select(t => t ?? "null").ToList(), rows);
    }

    /// <summary>A result set of documents.</summary>
    /// <param name="documents">The documents.</param>
    /// <param name="truncated">Whether more documents were left out.</param>
    /// <returns>The result.</returns>
    public static QueryResult Documents(IReadOnlyList<BsonDocument> documents, bool truncated = false)
    {
        var (columns, types, rows) = Flatten(documents);
        return new QueryResult
        {
            IsQuery = true,
            Columns = columns,
            ColumnTypes = types,
            Rows = rows,
            Truncated = truncated,
            ElapsedMilliseconds = 0,
        };
    }

    /// <summary>A one-column result set of values.</summary>
    /// <param name="column">The column name.</param>
    /// <param name="values">The values.</param>
    /// <returns>The result.</returns>
    public static QueryResult Values(string column, IReadOnlyList<BsonValue> values) => new()
    {
        IsQuery = true,
        Columns = [column],
        ColumnTypes = [values.FirstOrDefault(v => !v.IsBsonNull) is { } first ? MongoValues.TypeName(first) : "null"],
        Rows = values.Select(v => (IReadOnlyList<object?>)[MongoValues.ToCell(v)]).ToList(),
        ElapsedMilliseconds = 0,
    };

    /// <summary>A one-row, one-column result.</summary>
    /// <param name="column">The column name.</param>
    /// <param name="value">The value.</param>
    /// <returns>The result.</returns>
    public static QueryResult Value(string column, BsonValue value) => Values(column, [value]);

    /// <summary>A write's outcome: how many documents it changed (<c>-1</c> when not applicable).</summary>
    /// <param name="count">The count.</param>
    /// <returns>The result.</returns>
    public static QueryResult Affected(long count) => new() { IsQuery = false, RowsAffected = count, ElapsedMilliseconds = 0 };
}
