namespace Bengine.Introspection.Sql;

using Bengine.Enums;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Tree;

/// <summary>
/// Generates the SQL behind the object explorer's actions — drop, truncate,
/// rename, and the "SQL generator" templates (<c>SELECT</c>, <c>INSERT</c>,
/// <c>UPDATE</c>, <c>DELETE</c>) — in each provider's dialect. The explorer
/// shows the SQL for review before running it.
/// </summary>
public static class ObjectSql
{
    /// <summary>Gets whether an object kind can be dropped from the explorer.</summary>
    /// <param name="kind">The node kind.</param>
    /// <returns><c>true</c> if <see cref="Drop"/> supports it.</returns>
    public static bool CanDrop(NodeKind kind) => kind is NodeKind.Table or NodeKind.View or NodeKind.MaterializedView
        or NodeKind.Sequence or NodeKind.Index or NodeKind.Trigger or NodeKind.Function or NodeKind.Procedure
        or NodeKind.TriggerFunction or NodeKind.Schema or NodeKind.Database or NodeKind.Column or NodeKind.ForeignTable
        or NodeKind.Type or NodeKind.Domain;

    /// <summary>Builds the statement that drops an object.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="node">The object.</param>
    /// <param name="cascade">Whether to drop dependent objects too (PostgreSQL).</param>
    /// <returns>The statement.</returns>
    /// <exception cref="NotSupportedException">The object kind cannot be dropped here.</exception>
    public static string Drop(DatabaseProvider provider, NodeRef node, bool cascade = false)
    {
        var name = node.Name ?? throw new NotSupportedException("A folder cannot be dropped.");
        var q = Qualified(provider, node.Schema, name);
        var tail = cascade && provider == DatabaseProvider.PostgreSql ? " CASCADE" : string.Empty;
        var relation = node.Relation is { } r ? Qualified(provider, node.Schema, r) : null;
        return node.Kind switch
        {
            NodeKind.Table => $"DROP TABLE {q}{tail};",
            NodeKind.View => $"DROP VIEW {q}{tail};",
            NodeKind.MaterializedView => $"DROP MATERIALIZED VIEW {q}{tail};",
            NodeKind.ForeignTable => $"DROP FOREIGN TABLE {q}{tail};",
            NodeKind.Sequence => $"DROP SEQUENCE {q}{tail};",
            NodeKind.Type => $"DROP TYPE {q}{tail};",
            NodeKind.Domain => $"DROP DOMAIN {q}{tail};",
            NodeKind.Schema => $"DROP SCHEMA {Quote(provider, name)}{tail};",
            NodeKind.Database => $"DROP DATABASE {Quote(provider, name)};",
            NodeKind.Column => $"ALTER TABLE {relation} DROP COLUMN {Quote(provider, name)}{tail};",
            NodeKind.Index => provider switch
            {
                DatabaseProvider.MySql or DatabaseProvider.SqlServer => $"DROP INDEX {Quote(provider, name)} ON {relation};",
                _ => $"DROP INDEX {q}{tail};",
            },
            NodeKind.Trigger => provider switch
            {
                DatabaseProvider.PostgreSql => $"DROP TRIGGER {Quote(provider, name)} ON {relation}{tail};",
                _ => $"DROP TRIGGER {q};",
            },
            NodeKind.Function or NodeKind.TriggerFunction => provider == DatabaseProvider.PostgreSql
                ? $"DROP FUNCTION {q}({node.Signature}){tail};"
                : $"DROP FUNCTION {q};",
            NodeKind.Procedure => provider == DatabaseProvider.PostgreSql
                ? $"DROP PROCEDURE {q}({node.Signature}){tail};"
                : $"DROP PROCEDURE {q};",
            _ => throw new NotSupportedException($"{node.Kind} objects cannot be dropped from the explorer."),
        };
    }

    /// <summary>Builds the statement that empties a table.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="node">The table.</param>
    /// <returns>The statement.</returns>
    public static string Truncate(DatabaseProvider provider, NodeRef node)
    {
        var q = Qualified(provider, node.Schema, node.Name!);
        return provider == DatabaseProvider.Sqlite ? $"DELETE FROM {q};" : $"TRUNCATE TABLE {q};";
    }

    /// <summary>Gets whether an object kind can be renamed from the explorer.</summary>
    /// <param name="kind">The node kind.</param>
    /// <returns><c>true</c> if <see cref="Rename"/> supports it.</returns>
    public static bool CanRename(NodeKind kind) => kind is NodeKind.Table or NodeKind.View or NodeKind.Column or NodeKind.MaterializedView
        or NodeKind.Sequence or NodeKind.Index or NodeKind.Schema;

    /// <summary>Builds the statement that renames an object.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="node">The object.</param>
    /// <param name="newName">The new name.</param>
    /// <returns>The statement.</returns>
    /// <exception cref="NotSupportedException">The object kind cannot be renamed here.</exception>
    public static string Rename(DatabaseProvider provider, NodeRef node, string newName)
    {
        var name = node.Name!;
        var q = Qualified(provider, node.Schema, name);
        var to = Quote(provider, newName);
        if (provider == DatabaseProvider.SqlServer)
        {
            string Literal(string text) => "N'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
            return node.Kind switch
            {
                NodeKind.Column => $"EXEC sp_rename {Literal($"{node.Schema}.{node.Relation}.{name}")}, {Literal(newName)}, 'COLUMN';",
                NodeKind.Index => $"EXEC sp_rename {Literal($"{node.Schema}.{node.Relation}.{name}")}, {Literal(newName)}, 'INDEX';",
                NodeKind.Schema => throw new NotSupportedException("SQL Server cannot rename a schema."),
                _ => $"EXEC sp_rename {Literal($"{node.Schema}.{name}")}, {Literal(newName)};",
            };
        }

        return node.Kind switch
        {
            NodeKind.Column => $"ALTER TABLE {Qualified(provider, node.Schema, node.Relation!)} RENAME COLUMN {Quote(provider, name)} TO {to};",
            NodeKind.Table when provider == DatabaseProvider.MySql => $"RENAME TABLE {q} TO {Qualified(provider, node.Schema, newName)};",
            NodeKind.Table => $"ALTER TABLE {q} RENAME TO {to};",
            NodeKind.View when provider == DatabaseProvider.MySql => $"RENAME TABLE {q} TO {Qualified(provider, node.Schema, newName)};",
            NodeKind.View when provider == DatabaseProvider.PostgreSql => $"ALTER VIEW {q} RENAME TO {to};",
            NodeKind.MaterializedView => $"ALTER MATERIALIZED VIEW {q} RENAME TO {to};",
            NodeKind.Sequence => $"ALTER SEQUENCE {q} RENAME TO {to};",
            NodeKind.Index when provider == DatabaseProvider.PostgreSql => $"ALTER INDEX {q} RENAME TO {to};",
            NodeKind.Index when provider == DatabaseProvider.MySql => $"ALTER TABLE {Qualified(provider, node.Schema, node.Relation!)} RENAME INDEX {Quote(provider, name)} TO {to};",
            NodeKind.Schema when provider == DatabaseProvider.PostgreSql => $"ALTER SCHEMA {Quote(provider, name)} RENAME TO {to};",
            _ => throw new NotSupportedException($"Renaming a {node.Kind} is not supported on {provider}."),
        };
    }

    /// <summary>Generates a statement template for a relation, as an IDE's "SQL generator" does.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="relation">The relation, with its columns.</param>
    /// <param name="kind">select, insert, update, or delete.</param>
    /// <returns>The SQL.</returns>
    public static string Template(DatabaseProvider provider, CatalogRelation relation, string kind)
    {
        var q = Qualified(provider, relation.Schema, relation.Name);
        var columns = relation.Columns.Select(c => Quote(provider, c.Name)).ToList();
        var keys = relation.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        if (keys.Count == 0)
        {
            keys = relation.Columns.Take(1).Select(c => c.Name).ToList();
        }

        string Param(string column) => ":" + new string(column.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        var where = string.Join(" AND ", keys.Select(k => $"{Quote(provider, k)} = {Param(k)}"));
        var writable = relation.Columns.Where(c => !c.IsGenerated).ToList();
        var settable = writable.Where(c => !c.IsPrimaryKey).ToList();

        return kind switch
        {
            "insert" => $"INSERT INTO {q} ({string.Join(", ", writable.Select(c => Quote(provider, c.Name)))})\nVALUES ({string.Join(", ", writable.Select(c => Param(c.Name)))});",
            "update" => $"UPDATE {q}\nSET {string.Join(",\n    ", (settable.Count > 0 ? settable : writable).Select(c => $"{Quote(provider, c.Name)} = {Param(c.Name)}"))}\nWHERE {where};",
            "delete" => $"DELETE FROM {q}\nWHERE {where};",
            _ => provider == DatabaseProvider.SqlServer
                ? $"SELECT TOP 100 {string.Join(",\n       ", columns)}\nFROM {q};"
                : $"SELECT {string.Join(",\n       ", columns)}\nFROM {q}\nLIMIT 100;",
        };
    }

    /// <summary>Renders a possibly schema-qualified name, quoting only where needed.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="schema">The schema, or <c>null</c>.</param>
    /// <param name="name">The name.</param>
    /// <returns>The qualified name.</returns>
    public static string Qualified(DatabaseProvider provider, string? schema, string name) =>
        schema is { Length: > 0 } && provider != DatabaseProvider.Sqlite ? $"{Quote(provider, schema)}.{Quote(provider, name)}" : Quote(provider, name);

    private static string Quote(DatabaseProvider provider, string name) => SqlKeywords.QuoteIfNeeded(name, provider);
}
