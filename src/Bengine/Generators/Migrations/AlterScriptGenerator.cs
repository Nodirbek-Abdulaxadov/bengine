namespace Bengine.Generators.Migrations;

using Bengine.Diff;
using Bengine.Enums;
using Bengine.Generators.Sql;
using Bengine.Models;

/// <summary>
/// Renders <see cref="SchemaChange"/>s as DDL for one provider: <c>ALTER TABLE</c>
/// in each dialect's syntax (PostgreSQL <c>ALTER COLUMN … TYPE … USING</c>,
/// MySQL <c>MODIFY COLUMN</c>, SQL Server <c>ALTER COLUMN</c> and named default
/// constraints), and for SQLite — whose <c>ALTER TABLE</c> cannot change
/// columns or constraints — the documented rebuild: create the new table,
/// copy the rows, drop the old one, rename, and recreate its indexes.
/// </summary>
public sealed class AlterScriptGenerator
{
    private readonly SqlGeneratorBase _sql;

    /// <summary>Gets the SQL generator the statements are rendered with.</summary>
    public SqlGeneratorBase Sql => _sql;

    /// <summary>Creates a renderer over a provider's DDL generator.</summary>
    /// <param name="sql">The DDL generator.</param>
    public AlterScriptGenerator(SqlGeneratorBase sql) => _sql = sql;

    /// <summary>Gets the provider.</summary>
    public DatabaseProvider Provider => _sql.Provider;

    /// <summary>
    /// Gets or sets the statements to replay after a SQLite table rebuild — the
    /// table's triggers, which dropping the old table removes — by table name.
    /// </summary>
    public Func<string, IEnumerable<string>>? RebuildEpilogue { get; set; }

    /// <summary>Creates the renderer for a provider.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The renderer.</returns>
    public static AlterScriptGenerator For(DatabaseProvider provider) => new(provider switch
    {
        DatabaseProvider.PostgreSql => new PostgreSqlGenerator(),
        DatabaseProvider.MySql => new MySqlGenerator(),
        DatabaseProvider.SqlServer => new SqlServerGenerator(),
        DatabaseProvider.Sqlite => new SqliteGenerator(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider."),
    });

    /// <summary>Renders changes as statements.</summary>
    /// <param name="changes">The changes, in order.</param>
    /// <param name="source">The schema the changes start from (SQLite rebuilds read its tables).</param>
    /// <param name="target">The schema the changes lead to (SQLite rebuilds create its tables).</param>
    /// <returns>The statements, each ending in <c>;</c>.</returns>
    public IReadOnlyList<string> Render(IReadOnlyList<SchemaChange> changes, DatabaseSchema? source = null, DatabaseSchema? target = null)
    {
        var statements = new List<string>();

        // SQLite: every change to a table that needs a rebuild is folded into one rebuild,
        // emitted where the table's first such change stands.
        var rebuild = Provider == DatabaseProvider.Sqlite
            ? changes.Where(NeedsRebuild).Select(c => c.Subject).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var rebuilt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (rebuild.Contains(change.Subject) && change is not (CreateTableChange or DropTableChange or RenameTableChange))
            {
                if (NeedsRebuild(change) && rebuilt.Add(change.Subject))
                {
                    statements.AddRange(Rebuild(change, changes, source, target));
                }

                continue;
            }

            statements.AddRange(Render(change));
        }

        return statements;
    }

    /// <summary>Renders changes as one script.</summary>
    /// <param name="changes">The changes.</param>
    /// <param name="source">The source schema.</param>
    /// <param name="target">The target schema.</param>
    /// <returns>The script.</returns>
    public string Script(IReadOnlyList<SchemaChange> changes, DatabaseSchema? source = null, DatabaseSchema? target = null) =>
        string.Join(Environment.NewLine, Render(changes, source, target)) + (changes.Count > 0 ? Environment.NewLine : string.Empty);

    private IEnumerable<string> Render(SchemaChange change)
    {
        switch (change)
        {
            case CreateTableChange c:
                yield return _sql.CreateTable(c.Table);
                foreach (var statement in Comments(c.Table))
                {
                    yield return statement;
                }

                break;
            case DropTableChange c:
                yield return $"DROP TABLE {Name(c.Table)};";
                break;
            case RenameTableChange c:
                yield return Provider switch
                {
                    DatabaseProvider.MySql => $"RENAME TABLE {Name(c.Table)} TO {_sql.Qualify(c.Table.Schema, c.NewName)};",
                    DatabaseProvider.SqlServer => $"EXEC sp_rename {Literal($"{c.Table.Schema}.{c.Table.Name}")}, {Literal(c.NewName)};",
                    _ => $"ALTER TABLE {Name(c.Table)} RENAME TO {_sql.Quote(c.NewName)};",
                };
                break;
            case AddColumnChange c:
                yield return Provider == DatabaseProvider.SqlServer
                    ? $"ALTER TABLE {Name(c.Table)} ADD {Definition(c.Column)};"
                    : $"ALTER TABLE {Name(c.Table)} ADD COLUMN {Definition(c.Column)};";
                if (Provider == DatabaseProvider.PostgreSql && c.Column.Comment is { } columnComment)
                {
                    yield return $"COMMENT ON COLUMN {Name(c.Table)}.{_sql.Quote(c.Column.Name)} IS {Literal(columnComment)};";
                }

                break;
            case DropColumnChange c:
                if (Provider == DatabaseProvider.SqlServer)
                {
                    yield return DropSqlServerDefault(c.Table, c.Column.Name);
                }

                yield return $"ALTER TABLE {Name(c.Table)} DROP COLUMN {_sql.Quote(c.Column.Name)};";
                break;
            case RenameColumnChange c:
                yield return Provider == DatabaseProvider.SqlServer
                    ? $"EXEC sp_rename {Literal($"{c.Table.Schema}.{c.Table.Name}.{c.OldName}")}, {Literal(c.NewName)}, 'COLUMN';"
                    : $"ALTER TABLE {Name(c.Table)} RENAME COLUMN {_sql.Quote(c.OldName)} TO {_sql.Quote(c.NewName)};";
                break;
            case AlterColumnChange c:
                foreach (var statement in AlterColumn(c))
                {
                    yield return statement;
                }

                break;
            case AddPrimaryKeyChange c:
                yield return Provider == DatabaseProvider.MySql
                    ? $"ALTER TABLE {Name(c.Table)} ADD PRIMARY KEY ({_sql.QuoteList(c.Key.ColumnNames)});"
                    : $"ALTER TABLE {Name(c.Table)} ADD CONSTRAINT {_sql.Quote(KeyName(c.Table, c.Key))} PRIMARY KEY ({_sql.QuoteList(c.Key.ColumnNames)});";
                break;
            case DropPrimaryKeyChange c:
                yield return Provider switch
                {
                    DatabaseProvider.MySql => $"ALTER TABLE {Name(c.Table)} DROP PRIMARY KEY;",
                    DatabaseProvider.SqlServer when string.IsNullOrEmpty(c.Key.Name) => DynamicDrop(c.Table,
                        $"SELECT @n = name FROM sys.key_constraints WHERE type = 'PK' AND parent_object_id = OBJECT_ID({Literal(Plain(c.Table))})"),
                    _ => $"ALTER TABLE {Name(c.Table)} DROP CONSTRAINT {_sql.Quote(KeyName(c.Table, c.Key))};",
                };
                break;
            case AddForeignKeyChange c:
                yield return $"ALTER TABLE {Name(c.Table)} ADD {_sql.ForeignKeyConstraint(c.Key)};";
                break;
            case DropForeignKeyChange c:
                yield return Provider == DatabaseProvider.MySql
                    ? $"ALTER TABLE {Name(c.Table)} DROP FOREIGN KEY {_sql.Quote(c.Key.Name)};"
                    : $"ALTER TABLE {Name(c.Table)} DROP CONSTRAINT {_sql.Quote(c.Key.Name)};";
                break;
            case AddCheckChange c:
                yield return $"ALTER TABLE {Name(c.Table)} ADD {_sql.CheckConstraintClause(c.Check)};";
                break;
            case DropCheckChange c:
                yield return Provider == DatabaseProvider.MySql
                    ? $"ALTER TABLE {Name(c.Table)} DROP CHECK {_sql.Quote(c.Check.Name)};"
                    : $"ALTER TABLE {Name(c.Table)} DROP CONSTRAINT {_sql.Quote(c.Check.Name)};";
                break;
            case AddIndexChange c:
                yield return _sql.CreateIndex(c.Table, c.Index);
                break;
            case DropIndexChange c:
                yield return Provider switch
                {
                    DatabaseProvider.MySql or DatabaseProvider.SqlServer => $"DROP INDEX {_sql.Quote(c.Index.Name)} ON {Name(c.Table)};",
                    _ => $"DROP INDEX {_sql.Qualify(c.Table.Schema, c.Index.Name)};",
                };
                break;
            case TableCommentChange c:
                if (Provider == DatabaseProvider.PostgreSql)
                {
                    yield return $"COMMENT ON TABLE {Name(c.Table)} IS {(c.Table.Comment is { } t ? Literal(t) : "NULL")};";
                }
                else if (Provider == DatabaseProvider.MySql)
                {
                    yield return $"ALTER TABLE {Name(c.Table)} COMMENT = {Literal(c.Table.Comment ?? string.Empty)};";
                }

                break;
            case CreateViewChange c:
                yield return _sql.CreateView(c.View);
                break;
            case DropViewChange c:
                yield return $"DROP {(c.View.IsMaterialized ? "MATERIALIZED " : string.Empty)}VIEW {_sql.Qualify(c.View.Schema, c.View.Name)};";
                break;
            case CreateSequenceChange c:
                yield return _sql.CreateSequence(c.Sequence);
                break;
            case DropSequenceChange c:
                yield return $"DROP SEQUENCE {_sql.Qualify(c.Sequence.Schema, c.Sequence.Name)};";
                break;
        }
    }

    private IEnumerable<string> AlterColumn(AlterColumnChange c)
    {
        var table = Name(c.Table);
        var column = _sql.Quote(c.To.Name);

        // Making a column NOT NULL fails on existing NULLs; give them the new default first.
        if (c.Changes.HasFlag(ColumnChange.Nullability) && !c.To.IsNullable && _sql.Dialect.RenderDefault(c.To.Default) is { } fill)
        {
            yield return $"UPDATE {table} SET {column} = {fill} WHERE {column} IS NULL;";
        }

        switch (Provider)
        {
            case DatabaseProvider.PostgreSql:
                if (c.Changes.HasFlag(ColumnChange.Type))
                {
                    var type = _sql.ColumnType(c.To);
                    yield return $"ALTER TABLE {table} ALTER COLUMN {column} TYPE {type} USING {column}::{type};";
                }

                if (c.Changes.HasFlag(ColumnChange.Nullability))
                {
                    yield return $"ALTER TABLE {table} ALTER COLUMN {column} {(c.To.IsNullable ? "DROP" : "SET")} NOT NULL;";
                }

                if (c.Changes.HasFlag(ColumnChange.Default))
                {
                    yield return _sql.Dialect.RenderDefault(c.To.Default) is { } value
                        ? $"ALTER TABLE {table} ALTER COLUMN {column} SET DEFAULT {value};"
                        : $"ALTER TABLE {table} ALTER COLUMN {column} DROP DEFAULT;";
                }

                if (c.Changes.HasFlag(ColumnChange.Identity))
                {
                    yield return c.To.Identity.Strategy == IdentityStrategy.None
                        ? $"ALTER TABLE {table} ALTER COLUMN {column} DROP IDENTITY IF EXISTS;"
                        : $"ALTER TABLE {table} ALTER COLUMN {column} ADD GENERATED BY DEFAULT AS IDENTITY;";
                }

                if (c.Changes.HasFlag(ColumnChange.Generated))
                {
                    yield return c.To.Generated is { } generated
                        ? $"ALTER TABLE {table} ALTER COLUMN {column} SET EXPRESSION AS ({generated.Expression});"
                        : $"ALTER TABLE {table} ALTER COLUMN {column} DROP EXPRESSION;";
                }

                if (c.Changes.HasFlag(ColumnChange.Comment))
                {
                    yield return $"COMMENT ON COLUMN {table}.{column} IS {(c.To.Comment is { } comment ? Literal(comment) : "NULL")};";
                }

                break;

            case DatabaseProvider.MySql:
                // MODIFY restates the whole definition: type, nullability, default, auto-increment, comment.
                yield return $"ALTER TABLE {table} MODIFY COLUMN {Definition(c.To)};";
                break;

            case DatabaseProvider.SqlServer:
                if (c.Changes.HasFlag(ColumnChange.Type) || c.Changes.HasFlag(ColumnChange.Nullability))
                {
                    yield return $"ALTER TABLE {table} ALTER COLUMN {column} {_sql.ColumnType(c.To)} {(c.To.IsNullable ? "NULL" : "NOT NULL")};";
                }

                if (c.Changes.HasFlag(ColumnChange.Default))
                {
                    yield return DropSqlServerDefault(c.Table, c.To.Name);
                    if (_sql.Dialect.RenderDefault(c.To.Default) is { } value)
                    {
                        yield return $"ALTER TABLE {table} ADD CONSTRAINT {_sql.Quote($"DF_{c.Table.Name}_{c.To.Name}")} DEFAULT {value} FOR {column};";
                    }
                }

                if (c.Changes.HasFlag(ColumnChange.Identity) || c.Changes.HasFlag(ColumnChange.Generated))
                {
                    yield return $"-- SQL Server cannot change the identity or computation of {table}.{column} in place; recreate the column.";
                }

                break;
        }
    }

    /// <summary>Gets whether SQLite needs a table rebuild for a change (its ALTER TABLE cannot express it).</summary>
    /// <param name="change">The change.</param>
    /// <returns><c>true</c> when the table must be rebuilt.</returns>
    public static bool NeedsRebuild(SchemaChange change) => change switch
    {
        AlterColumnChange or AddPrimaryKeyChange or DropPrimaryKeyChange or AddForeignKeyChange or DropForeignKeyChange
            or AddCheckChange or DropCheckChange => true,
        AddColumnChange c => !c.Column.IsNullable && c.Column.Default.Kind == DefaultValueKind.None
            || c.Table.PrimaryKey?.ColumnNames.Contains(c.Column.Name, StringComparer.OrdinalIgnoreCase) == true,
        DropColumnChange c => c.Table.PrimaryKey?.ColumnNames.Contains(c.Column.Name, StringComparer.OrdinalIgnoreCase) == true
            || c.Table.Indexes.Any(i => i.Columns.Any(x => x.ColumnName.Equals(c.Column.Name, StringComparison.OrdinalIgnoreCase)))
            || c.Table.ForeignKeys.Any(f => f.Columns.Contains(c.Column.Name, StringComparer.OrdinalIgnoreCase)),
        _ => false,
    };

    // SQLite's twelve-step table rebuild (https://sqlite.org/lang_altertable.html#otheralter).
    private IEnumerable<string> Rebuild(SchemaChange change, IReadOnlyList<SchemaChange> all, DatabaseSchema? source, DatabaseSchema? target)
    {
        var subject = change.Subject;
        var before = Find(source, subject) ?? TableOf(change, preferSource: true)!;
        var after = Find(target, subject) ?? TableOf(change, preferSource: false) ?? before;
        var renames = all.OfType<RenameColumnChange>().Where(r => r.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(r => r.NewName, r => r.OldName, StringComparer.OrdinalIgnoreCase);

        var copied = after.Columns
            .Where(c => !c.IsGenerated)
            .Select(c => (Target: c.Name, Source: renames.TryGetValue(c.Name, out var old) ? old : c.Name, Column: c))
            .Where(x => before.Columns.Any(s => s.Name.Equals(x.Source, StringComparison.OrdinalIgnoreCase) && !s.IsGenerated))
            .ToList();

        // A column that becomes NOT NULL takes its default where the old rows held NULL.
        string Select((string Target, string Source, Column Column) x) =>
            !x.Column.IsNullable && _sql.Dialect.RenderDefault(x.Column.Default) is { } fill
                ? $"COALESCE({_sql.Quote(x.Source)}, {fill})"
                : _sql.Quote(x.Source);
        var temp = after.Name + "__bengine_new";
        var rebuilt = SchemaDiffer.Copy(after, name: temp);

        yield return "PRAGMA foreign_keys = OFF;";
        yield return _sql.CreateTable(rebuilt).Replace("CREATE TABLE IF NOT EXISTS", "CREATE TABLE", StringComparison.Ordinal);
        if (copied.Count > 0)
        {
            yield return $"INSERT INTO {_sql.Quote(temp)} ({_sql.QuoteList(copied.Select(c => c.Target))}) " +
                         $"SELECT {string.Join(", ", copied.Select(Select))} FROM {_sql.Quote(before.Name)};";
        }

        yield return $"DROP TABLE {_sql.Quote(before.Name)};";
        yield return $"ALTER TABLE {_sql.Quote(temp)} RENAME TO {_sql.Quote(after.Name)};";
        foreach (var index in after.Indexes)
        {
            yield return _sql.CreateIndex(after, index);
        }

        foreach (var statement in RebuildEpilogue?.Invoke(after.Name) ?? [])
        {
            yield return statement.TrimEnd().TrimEnd(';') + ";";
        }

        yield return "PRAGMA foreign_keys = ON;";
    }

    private static Table? Find(DatabaseSchema? schema, string subject) =>
        schema?.Tables.FirstOrDefault(t => (t.Schema is null ? t.Name : $"{t.Schema}.{t.Name}").Equals(subject, StringComparison.OrdinalIgnoreCase));

    private static Table? TableOf(SchemaChange change, bool preferSource) => change switch
    {
        AlterColumnChange c => c.Table,
        AddColumnChange c => c.Table,
        DropColumnChange c => c.Table,
        AddPrimaryKeyChange c => c.Table,
        DropPrimaryKeyChange c => c.Table,
        AddForeignKeyChange c => c.Table,
        DropForeignKeyChange c => c.Table,
        AddCheckChange c => c.Table,
        DropCheckChange c => c.Table,
        _ => null,
    };

    private IEnumerable<string> Comments(Table table)
    {
        if (Provider != DatabaseProvider.PostgreSql)
        {
            yield break;
        }

        if (table.Comment is { } comment)
        {
            yield return $"COMMENT ON TABLE {Name(table)} IS {Literal(comment)};";
        }

        foreach (var column in table.Columns.Where(c => c.Comment is not null))
        {
            yield return $"COMMENT ON COLUMN {Name(table)}.{_sql.Quote(column.Name)} IS {Literal(column.Comment!)};";
        }
    }

    private string Definition(Column column)
    {
        var definition = _sql.ColumnDefinition(column);
        return Provider == DatabaseProvider.MySql && column.Comment is { } comment ? $"{definition} COMMENT {Literal(comment)}" : definition;
    }

    private string KeyName(Table table, PrimaryKey key) =>
        string.IsNullOrEmpty(key.Name) ? Provider == DatabaseProvider.PostgreSql ? $"{table.Name}_pkey" : $"PK_{table.Name}" : key.Name;

    // SQL Server names default constraints itself; find and drop the column's one.
    private string DropSqlServerDefault(Table table, string column) => DynamicDrop(table,
        $"SELECT @n = d.name FROM sys.default_constraints d JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id " +
        $"WHERE d.parent_object_id = OBJECT_ID({Literal(Plain(table))}) AND c.name = {Literal(column)}");

    private string DynamicDrop(Table table, string lookup) =>
        $"DECLARE @n sysname; {lookup}; IF @n IS NOT NULL EXEC('ALTER TABLE {Name(table).Replace("'", "''", StringComparison.Ordinal)} DROP CONSTRAINT [' + @n + ']');";

    private static string Plain(Table table) => table.Schema is null ? table.Name : $"{table.Schema}.{table.Name}";

    private string Name(Table table) => _sql.Qualify(table.Schema, table.Name);

    private string Literal(string text) =>
        (Provider == DatabaseProvider.SqlServer ? "N'" : "'") + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}
