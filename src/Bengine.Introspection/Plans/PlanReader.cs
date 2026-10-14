namespace Bengine.Introspection.Plans;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bengine.Enums;
using Bengine.Introspection.Model;
using Bengine.Introspection.Sql;

/// <summary>
/// Gets a statement's execution plan from the server and parses it into a
/// <see cref="QueryPlan"/>: PostgreSQL <c>EXPLAIN (FORMAT JSON)</c>, MySQL
/// <c>EXPLAIN FORMAT=TREE</c> / <c>EXPLAIN ANALYZE</c> (MariaDB and older MySQL:
/// the tabular <c>EXPLAIN</c> / <c>ANALYZE</c>), SQL Server showplan XML, and
/// SQLite <c>EXPLAIN QUERY PLAN</c>. Analyzing executes the statement; a
/// modifying statement is analyzed inside a transaction that is rolled back.
/// </summary>
public static partial class PlanReader
{
    /// <summary>Explains a statement.</summary>
    /// <param name="session">The session.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="analyze">Whether to execute it and report actual rows and times.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="IntrospectionException">The server refused the statement.</exception>
    public static async Task<QueryPlan> ExplainAsync(DatabaseSession session, string sql, bool analyze, CancellationToken cancellationToken = default)
    {
        sql = sql.Trim().TrimEnd(';');
        var info = SqlStatementInfo.Classify(sql, session.Provider);
        var rollback = analyze && info.CanModify && !session.InTransaction;
        if (rollback)
        {
            await session.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var plan = session.Provider switch
            {
                DatabaseProvider.PostgreSql => await PostgresAsync(session, sql, analyze, cancellationToken).ConfigureAwait(false),
                DatabaseProvider.MySql => await MySqlAsync(session, sql, analyze, cancellationToken).ConfigureAwait(false),
                DatabaseProvider.SqlServer => await SqlServerAsync(session, sql, analyze, cancellationToken).ConfigureAwait(false),
                _ => await SqliteAsync(session, sql, cancellationToken).ConfigureAwait(false),
            };
            plan.Annotate();
            return plan;
        }
        finally
        {
            if (rollback && session.InTransaction)
            {
                await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    // ------------------------------------------------------------------ PostgreSQL

    private static async Task<QueryPlan> PostgresAsync(DatabaseSession session, string sql, bool analyze, CancellationToken ct)
    {
        var options = analyze ? "FORMAT JSON, ANALYZE, BUFFERS" : "FORMAT JSON";
        var result = await session.Query.ExecuteAsync($"EXPLAIN ({options}) {sql}", 10, ct).ConfigureAwait(false);
        var json = string.Concat(result.Rows.Select(r => r[0]?.ToString()));
        return ParsePostgres(json, analyze);
    }

    /// <summary>Parses PostgreSQL's <c>EXPLAIN (FORMAT JSON)</c> output.</summary>
    /// <param name="json">The JSON.</param>
    /// <param name="analyzed">Whether it came from <c>EXPLAIN ANALYZE</c>.</param>
    /// <returns>The plan.</returns>
    public static QueryPlan ParsePostgres(string json, bool analyzed)
    {
        var plan = new QueryPlan { Raw = json, RawFormat = "json", Analyzed = analyzed };
        using var document = JsonDocument.Parse(json);
        foreach (var statement in document.RootElement.EnumerateArray())
        {
            if (statement.TryGetProperty("Plan", out var root))
            {
                plan.Roots.Add(PostgresNode(root));
            }

            if (statement.TryGetProperty("Planning Time", out var planning))
            {
                plan.PlanningTimeMs = planning.GetDouble();
            }

            if (statement.TryGetProperty("Execution Time", out var execution))
            {
                plan.ExecutionTimeMs = execution.GetDouble();
            }
        }

        return plan;
    }

    private static PlanNode PostgresNode(JsonElement element)
    {
        string? Text(string name) => element.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Array ? string.Join(", ", v.EnumerateArray()) : v.ToString() : null;
        double? Number(string name) => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        var operation = Text("Node Type") ?? "?";
        if (Text("Join Type") is { } joinType && (operation.Contains("Join", StringComparison.Ordinal) || operation == "Nested Loop"))
        {
            operation = $"{operation} ({joinType})";
        }

        if (Text("Strategy") is { } strategy && operation == "Aggregate")
        {
            operation = $"{strategy} Aggregate";
        }

        var relation = Text("Relation Name");
        var node = new PlanNode
        {
            Operation = operation,
            Object = relation is null ? Text("Index Name") ?? Text("CTE Name") ?? Text("Function Name")
                : (Text("Alias") is { } alias && alias != relation ? $"{relation} {alias}" : relation) + (Text("Index Name") is { } index ? $" using {index}" : string.Empty),
            StartupCost = Number("Startup Cost"),
            TotalCost = Number("Total Cost"),
            EstimatedRows = Number("Plan Rows"),
            ActualRows = Number("Actual Rows"),
            ActualTimeMs = Number("Actual Total Time"),
            Loops = Number("Actual Loops"),
        };

        foreach (var key in new[] { "Index Cond", "Recheck Cond", "Filter", "Hash Cond", "Merge Cond", "Join Filter", "Sort Key", "Sort Method", "Group Key", "Rows Removed by Filter", "Rows Removed by Index Recheck", "Heap Fetches", "Shared Hit Blocks", "Shared Read Blocks", "Workers Planned", "Peak Memory Usage" })
        {
            if (Text(key) is { Length: > 0 } value && value != "0")
            {
                node.Details.Add(new(key, value));
            }
        }

        if (element.TryGetProperty("Plans", out var children))
        {
            node.Children.AddRange(children.EnumerateArray().Select(PostgresNode));
        }

        return node;
    }

    // ------------------------------------------------------------------ MySQL / MariaDB

    private static async Task<QueryPlan> MySqlAsync(DatabaseSession session, string sql, bool analyze, CancellationToken ct)
    {
        var version = (await session.Query.ExecuteAsync("SELECT VERSION()", 1, ct).ConfigureAwait(false)).Rows[0][0]?.ToString() ?? string.Empty;
        var mariaDb = version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
        if (!mariaDb && SupportsTree(version))
        {
            var text = await SingleTextAsync(session, analyze ? $"EXPLAIN ANALYZE {sql}" : $"EXPLAIN FORMAT=TREE {sql}", ct).ConfigureAwait(false);
            return ParseMySqlTree(text, analyze);
        }

        var result = await session.Query.ExecuteAsync(analyze && mariaDb ? $"ANALYZE {sql}" : $"EXPLAIN {sql}", 1000, ct).ConfigureAwait(false);
        return ParseMySqlTable(result, analyze && mariaDb);
    }

    private static bool SupportsTree(string version)
    {
        var match = Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)");
        if (!match.Success)
        {
            return false;
        }

        var (major, minor, patch) = (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
        return major > 8 || (major == 8 && (minor > 0 || patch >= 18));
    }

    /// <summary>Parses MySQL's <c>EXPLAIN FORMAT=TREE</c> / <c>EXPLAIN ANALYZE</c> text.</summary>
    /// <param name="text">The plan text.</param>
    /// <param name="analyzed">Whether actual figures are present.</param>
    /// <returns>The plan.</returns>
    public static QueryPlan ParseMySqlTree(string text, bool analyzed)
    {
        var plan = new QueryPlan { Raw = text, RawFormat = "text", Analyzed = analyzed };
        var stack = new Stack<(PlanNode Node, int Indent)>();
        foreach (var line in text.Split('\n'))
        {
            var arrow = line.IndexOf("-> ", StringComparison.Ordinal);
            if (arrow < 0)
            {
                continue;
            }

            var node = MySqlTreeNode(line[(arrow + 3)..].TrimEnd());
            while (stack.Count > 0 && stack.Peek().Indent >= arrow)
            {
                stack.Pop();
            }

            if (stack.Count == 0)
            {
                plan.Roots.Add(node);
            }
            else
            {
                stack.Peek().Node.Children.Add(node);
            }

            stack.Push((node, arrow));
        }

        return plan;
    }

    private static PlanNode MySqlTreeNode(string text)
    {
        double? Number(string? value) => value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
        var cost = CostPattern().Match(text);
        var actual = ActualPattern().Match(text);
        var label = text;
        var cut = new[] { cost.Success ? cost.Index : -1, actual.Success ? actual.Index : -1 }.Where(i => i >= 0).DefaultIfEmpty(text.Length).Min();
        label = text[..cut].TrimEnd();

        string operation = label;
        string? obj = null;
        string? condition = null;
        var on = label.IndexOf(" on ", StringComparison.Ordinal);
        if (on > 0 && !label.StartsWith("Filter", StringComparison.Ordinal))
        {
            operation = label[..on];
            var rest = label[(on + 4)..];
            var paren = rest.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0)
            {
                condition = rest[(paren + 1)..];
                rest = rest[..paren];
            }

            obj = rest;
        }
        else if (label.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0)
        {
            operation = label[..colon];
            condition = label[(colon + 2)..];
        }

        var node = new PlanNode
        {
            Operation = operation,
            Object = obj,
            TotalCost = cost.Success ? Number(cost.Groups["cost"].Value) : null,
            EstimatedRows = cost.Success ? Number(cost.Groups["rows"].Value) : null,
            ActualTimeMs = actual.Success ? Number(actual.Groups["time"].Value) : null,
            ActualRows = actual.Success ? Number(actual.Groups["rows"].Value) : null,
            Loops = actual.Success ? Number(actual.Groups["loops"].Value) : null,
        };
        if (condition is not null)
        {
            node.Details.Add(new("Condition", condition));
        }

        return node;
    }

    private static QueryPlan ParseMySqlTable(QueryResult result, bool analyzed)
    {
        var plan = new QueryPlan { Raw = string.Join("\n", result.Rows.Select(r => string.Join(" | ", r))), Analyzed = analyzed };
        var root = new PlanNode { Operation = "Query" };
        int Index(string name) => result.Columns.ToList().FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        string? Cell(IReadOnlyList<object?> row, string name) => Index(name) is var i and >= 0 ? row[i]?.ToString() : null;
        foreach (var row in result.Rows)
        {
            var node = new PlanNode
            {
                Operation = $"{Cell(row, "type") ?? "?"} ({Cell(row, "select_type")})",
                Object = Cell(row, "table"),
                EstimatedRows = double.TryParse(Cell(row, "rows"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rows) ? rows : null,
                ActualRows = double.TryParse(Cell(row, "r_rows"), NumberStyles.Float, CultureInfo.InvariantCulture, out var actual) ? actual : null,
            };
            foreach (var key in new[] { "key", "possible_keys", "ref", "filtered", "Extra" })
            {
                if (Cell(row, key) is { Length: > 0 } value)
                {
                    node.Details.Add(new(key, value));
                }
            }

            root.Children.Add(node);
        }

        plan.Roots.Add(root);
        return plan;
    }

    // ------------------------------------------------------------------ SQL Server

    private static async Task<QueryPlan> SqlServerAsync(DatabaseSession session, string sql, bool analyze, CancellationToken ct)
    {
        var option = analyze ? "STATISTICS XML" : "SHOWPLAN_XML";
        await session.Query.ExecuteAllAsync($"SET {option} ON", 1, ct).ConfigureAwait(false);
        try
        {
            var results = await session.Query.ExecuteAllAsync(sql, 1, ct).ConfigureAwait(false);
            var xml = results.Where(r => r.IsQuery && r.Columns.Count == 1 && r.Rows.Count > 0 && r.Rows[0][0]?.ToString()?.Contains("ShowPlanXML", StringComparison.Ordinal) == true)
                .Select(r => r.Rows[0][0]!.ToString()!)
                .LastOrDefault() ?? throw new IntrospectionException("SQL Server returned no plan.");
            return ParseSqlServer(xml, analyze);
        }
        finally
        {
            await session.Query.ExecuteAllAsync($"SET {option} OFF", 1, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Parses SQL Server showplan XML.</summary>
    /// <param name="xml">The XML.</param>
    /// <param name="analyzed">Whether it is an actual (statistics) plan.</param>
    /// <returns>The plan.</returns>
    public static QueryPlan ParseSqlServer(string xml, bool analyzed)
    {
        var plan = new QueryPlan { Raw = xml, RawFormat = "xml", Analyzed = analyzed };
        var document = XDocument.Parse(xml);
        foreach (var statement in document.Descendants().Where(e => e.Name.LocalName == "QueryPlan"))
        {
            if (statement.Elements().FirstOrDefault(e => e.Name.LocalName == "RelOp") is { } root)
            {
                plan.Roots.Add(SqlServerNode(root));
            }
        }

        return plan;
    }

    private static PlanNode SqlServerNode(XElement relOp)
    {
        double? Number(XElement e, string name) => double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
        var physical = (string?)relOp.Attribute("PhysicalOp") ?? "?";
        var logical = (string?)relOp.Attribute("LogicalOp");
        var obj = relOp.Descendants().FirstOrDefault(e => e.Name.LocalName == "Object" && NearestRelOp(e) == relOp);
        var node = new PlanNode
        {
            Operation = logical is not null && logical != physical ? $"{physical} ({logical})" : physical,
            Object = obj is null ? null : string.Join(".", new[] { (string?)obj.Attribute("Schema"), (string?)obj.Attribute("Table") }.Where(s => s is not null).Select(s => s!.Trim('[', ']')))
                + ((string?)obj.Attribute("Index") is { } index ? $" using {index.Trim('[', ']')}" : string.Empty),
            TotalCost = Number(relOp, "EstimatedTotalSubtreeCost"),
            EstimatedRows = Number(relOp, "EstimateRows"),
        };

        var counters = relOp.Elements().FirstOrDefault(e => e.Name.LocalName == "RunTimeInformation")?.Elements().ToList();
        if (counters is { Count: > 0 })
        {
            node.ActualRows = counters.Sum(c => Number(c, "ActualRows") ?? 0);
            var executions = counters.Sum(c => Number(c, "ActualExecutions") ?? 0);
            node.Loops = executions;
            node.ActualRows = executions > 0 ? node.ActualRows / executions : node.ActualRows;
            node.ActualTimeMs = counters.Max(c => Number(c, "ActualElapsedms")) is { } elapsed && executions > 0 ? elapsed / executions : null;
        }

        var predicate = relOp.Descendants().FirstOrDefault(e => e.Name.LocalName is "Predicate" or "SeekPredicates" && NearestRelOp(e) == relOp);
        if (predicate?.Descendants().FirstOrDefault(e => e.Name.LocalName == "ScalarOperator") is { } scalar && (string?)scalar.Attribute("ScalarString") is { } text)
        {
            node.Details.Add(new(predicate.Name.LocalName == "Predicate" ? "Predicate" : "Seek", text));
        }

        node.Children.AddRange(relOp.Descendants().Where(e => e.Name.LocalName == "RelOp" && NearestRelOp(e.Parent!) == relOp).Select(SqlServerNode));
        return node;
    }

    private static XElement? NearestRelOp(XElement element)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            if (current.Name.LocalName == "RelOp")
            {
                return current;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ SQLite

    private static async Task<QueryPlan> SqliteAsync(DatabaseSession session, string sql, CancellationToken ct)
    {
        var result = await session.Query.ExecuteAsync($"EXPLAIN QUERY PLAN {sql}", 1000, ct).ConfigureAwait(false);
        var plan = new QueryPlan { Raw = string.Join("\n", result.Rows.Select(r => r[3]?.ToString())) };
        var nodes = new Dictionary<long, PlanNode>();
        foreach (var row in result.Rows)
        {
            var id = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
            var parent = Convert.ToInt64(row[1], CultureInfo.InvariantCulture);
            var detail = row[3]?.ToString() ?? string.Empty;
            var match = Regex.Match(detail, @"^(?<op>SCAN|SEARCH)\s+(?<obj>\S+)(?<rest>.*)$");
            var node = match.Success
                ? new PlanNode { Operation = match.Groups["op"].Value + (match.Groups["rest"].Value.Contains("COVERING INDEX", StringComparison.Ordinal) ? " (covering index)" : match.Groups["rest"].Value.Contains("INDEX", StringComparison.Ordinal) ? " (index)" : string.Empty), Object = match.Groups["obj"].Value }
                : new PlanNode { Operation = detail };
            if (match.Success && match.Groups["rest"].Value.Trim() is { Length: > 0 } rest)
            {
                node.Details.Add(new("Access", rest));
            }

            nodes[id] = node;
            if (nodes.TryGetValue(parent, out var owner))
            {
                owner.Children.Add(node);
            }
            else
            {
                plan.Roots.Add(node);
            }
        }

        return plan;
    }

    private static async Task<string> SingleTextAsync(DatabaseSession session, string sql, CancellationToken ct)
    {
        var result = await session.Query.ExecuteAsync(sql, 10, ct).ConfigureAwait(false);
        return string.Join("\n", result.Rows.Select(r => r[0]?.ToString()));
    }

    [GeneratedRegex(@"\(cost=(?<cost>[\d.e+-]+)\s+rows=(?<rows>[\d.e+-]+)\)")]
    private static partial Regex CostPattern();

    [GeneratedRegex(@"\(actual time=[\d.e+-]+\.\.(?<time>[\d.e+-]+)\s+rows=(?<rows>[\d.e+-]+)\s+loops=(?<loops>[\d.e+-]+)\)")]
    private static partial Regex ActualPattern();
}
