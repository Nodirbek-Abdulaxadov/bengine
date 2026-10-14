namespace Bengine.Introspection.Sql;

using Bengine.Enums;

/// <summary>A place an object is referenced.</summary>
/// <param name="Kind">What references it: View, Function, Trigger, File, Console…</param>
/// <param name="Name">The referencing object's (or file's) name.</param>
/// <param name="Line">The 1-based line of the first reference, when known.</param>
/// <param name="Snippet">The line of text around the reference.</param>
/// <param name="Definition">The referencing object's full definition, for database objects.</param>
public sealed record Usage(string Kind, string Name, int? Line, string Snippet, string? Definition = null);

/// <summary>
/// Finds where an object is used, for "Find Usages" and safe renames: inside
/// the database (views, routines, and triggers whose definitions mention it,
/// read from each provider's catalog) and in SQL text (open consoles and
/// project files), where only identifier tokens count — a match inside a
/// string or a comment is not a usage.
/// </summary>
public static class ObjectUsages
{
    /// <summary>Finds identifier occurrences of a name in SQL text.</summary>
    /// <param name="sql">The SQL text.</param>
    /// <param name="name">The object name.</param>
    /// <param name="provider">The dialect.</param>
    /// <returns>The (start, length) of each occurrence.</returns>
    public static IReadOnlyList<(int Start, int Length)> FindInText(string sql, string name, DatabaseProvider provider) =>
        SqlLexer.Lex(sql, provider)
            .Where(l => l.IsName && (l.Kind == SqlLexemeKind.QuotedIdentifier ? l.Identifier == name : l.Identifier.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Select(l => (l.Start, l.Length))
            .ToList();

    /// <summary>Renames identifier occurrences of a name in SQL text, quoting the new name when needed.</summary>
    /// <param name="sql">The SQL text.</param>
    /// <param name="oldName">The current name.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="provider">The dialect.</param>
    /// <returns>The new text and the number of replacements.</returns>
    public static (string Text, int Count) RenameInText(string sql, string oldName, string newName, DatabaseProvider provider)
    {
        var hits = FindInText(sql, oldName, provider);
        var replacement = SqlKeywords.QuoteIfNeeded(newName, provider);
        var text = sql;
        foreach (var (start, length) in hits.Reverse())
        {
            text = text.Remove(start, length).Insert(start, replacement);
        }

        return (text, hits.Count);
    }

    /// <summary>Describes the usages of a name in a text, one per line that mentions it.</summary>
    /// <param name="kind">The source kind (File, Console).</param>
    /// <param name="source">The source name.</param>
    /// <param name="sql">The text.</param>
    /// <param name="name">The object name.</param>
    /// <param name="provider">The dialect.</param>
    /// <returns>The usages.</returns>
    public static IEnumerable<Usage> InText(string kind, string source, string sql, string name, DatabaseProvider provider)
    {
        var lines = sql.Split('\n');
        return FindInText(sql, name, provider)
            .Select(hit => sql[..hit.Start].Count(c => c == '\n'))
            .Distinct()
            .Select(line => new Usage(kind, source, line + 1, lines[line].Trim()));
    }

    /// <summary>Finds the database objects whose definitions mention a name.</summary>
    /// <param name="session">A session on the database.</param>
    /// <param name="name">The object name.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The usages.</returns>
    public static async Task<IReadOnlyList<Usage>> InDatabaseAsync(DatabaseSession session, string name, CancellationToken cancellationToken = default)
    {
        if (session.Provider == DatabaseProvider.Oracle)
        {
            return await OracleUsagesAsync(session, name, cancellationToken).ConfigureAwait(false);
        }

        var sql = session.Provider switch
        {
            DatabaseProvider.PostgreSql => """
                SELECT 'View', schemaname || '.' || viewname, definition FROM pg_views WHERE schemaname NOT IN ('pg_catalog', 'information_schema')
                UNION ALL SELECT 'Materialized View', schemaname || '.' || matviewname, definition FROM pg_matviews
                UNION ALL SELECT CASE p.prokind WHEN 'p' THEN 'Procedure' ELSE 'Function' END, n.nspname || '.' || p.proname, p.prosrc
                    FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND p.prokind IN ('f', 'p')
                UNION ALL SELECT 'Trigger', c.relname || '.' || t.tgname, pg_get_triggerdef(t.oid)
                    FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE NOT t.tgisinternal
                """,
            DatabaseProvider.MySql => """
                SELECT 'View', CONCAT(table_schema, '.', table_name), view_definition FROM information_schema.views WHERE table_schema = DATABASE()
                UNION ALL SELECT routine_type, CONCAT(routine_schema, '.', routine_name), routine_definition FROM information_schema.routines WHERE routine_schema = DATABASE()
                UNION ALL SELECT 'Trigger', CONCAT(event_object_table, '.', trigger_name), action_statement FROM information_schema.triggers WHERE trigger_schema = DATABASE()
                """,
            DatabaseProvider.SqlServer => """
                SELECT CASE o.type WHEN 'V' THEN 'View' WHEN 'P' THEN 'Procedure' WHEN 'TR' THEN 'Trigger' ELSE 'Function' END,
                       OBJECT_SCHEMA_NAME(m.object_id) + '.' + OBJECT_NAME(m.object_id), m.definition
                FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id WHERE o.is_ms_shipped = 0
                """,
            _ => "SELECT CASE type WHEN 'view' THEN 'View' ELSE 'Trigger' END, name, sql FROM sqlite_master WHERE type IN ('view', 'trigger')",
        };

        var result = await session.Query.ExecuteAsync(sql, 50_000, cancellationToken).ConfigureAwait(false);
        var usages = new List<Usage>();
        foreach (var row in result.Rows)
        {
            var definition = row[2]?.ToString();
            if (string.IsNullOrEmpty(definition) || !definition.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            usages.AddRange(InText(row[0]?.ToString() ?? "Object", row[1]?.ToString() ?? "?", definition, name, session.Provider)
                .Take(1)
                .Select(u => u with { Definition = definition }));
        }

        return usages;
    }
    // Oracle: LONG columns (view text, trigger bodies) cannot be combined in one query, and
    // stored PL/SQL lives line by line in ALL_SOURCE.
    private static async Task<IReadOnlyList<Usage>> OracleUsagesAsync(DatabaseSession session, string name, CancellationToken cancellationToken)
    {
        const string Owners = "IN (SELECT username FROM all_users WHERE oracle_maintained = 'N' OR username = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))";
        var usages = new List<Usage>();
        foreach (var (kind, sql) in new[]
        {
            ("View", $"SELECT owner || '.' || view_name, text FROM all_views WHERE owner {Owners}"),
            ("Trigger", $"SELECT owner || '.' || trigger_name, trigger_body FROM all_triggers WHERE owner {Owners}"),
        })
        {
            var result = await session.Query.ExecuteAsync(sql, 50_000, cancellationToken).ConfigureAwait(false);
            foreach (var row in result.Rows)
            {
                if (row[1]?.ToString() is { } definition && definition.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    usages.AddRange(InText(kind, row[0]?.ToString() ?? "?", definition, name, DatabaseProvider.Oracle).Take(1).Select(u => u with { Definition = definition }));
                }
            }
        }

        var literal = name.ToUpperInvariant().Replace("'", "''", StringComparison.Ordinal);
        var source = await session.Query.ExecuteAsync(
            $"""
            SELECT INITCAP(type), owner || '.' || name, line, text FROM all_source
            WHERE owner {Owners} AND UPPER(text) LIKE '%{literal}%'
            ORDER BY owner, name, type, line
            """,
            50_000,
            cancellationToken).ConfigureAwait(false);
        foreach (var unit in source.Rows.GroupBy(r => (Kind: r[0]?.ToString() ?? "Object", Name: r[1]?.ToString() ?? "?")))
        {
            var hit = unit.FirstOrDefault(r => FindInText(r[3]?.ToString() ?? string.Empty, name, DatabaseProvider.Oracle).Count > 0);
            if (hit is not null)
            {
                usages.Add(new Usage(unit.Key.Kind, unit.Key.Name, Convert.ToInt32(hit[2], System.Globalization.CultureInfo.InvariantCulture), (hit[3]?.ToString() ?? string.Empty).Trim()));
            }
        }

        return usages;
    }

}
