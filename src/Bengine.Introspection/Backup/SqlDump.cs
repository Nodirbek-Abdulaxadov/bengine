namespace Bengine.Introspection.Backup;

using System.Text;
using Bengine.Diff;
using Bengine.Enums;
using Bengine.Generators.Migrations;
using Bengine.Introspection.Export;
using Bengine.Models;

/// <summary>What a Bengine SQL dump contains.</summary>
public sealed record SqlDumpOptions
{
    /// <summary>Gets a value indicating whether to write the DDL.</summary>
    public bool Schema { get; init; } = true;

    /// <summary>Gets a value indicating whether to write the rows as <c>INSERT</c>s.</summary>
    public bool Data { get; init; } = true;

    /// <summary>Gets the tables to dump, or <c>null</c> for all.</summary>
    public IReadOnlyCollection<string>? Tables { get; init; }

    /// <summary>Gets the rows per multi-row <c>INSERT</c>.</summary>
    public int RowsPerInsert { get; init; } = 100;
}

/// <summary>What a dump wrote.</summary>
/// <param name="Tables">The tables.</param>
/// <param name="Rows">The rows.</param>
/// <param name="Statements">The statements.</param>
public sealed record SqlDumpSummary(int Tables, long Rows, int Statements);

/// <summary>
/// A portable logical dump that needs no client tools: the schema rendered
/// from the introspected AST by Bengine's own generators (tables first, then
/// the rows as multi-row <c>INSERT</c>s, then indexes, foreign keys, and
/// views, so the script loads in any order of data), in the source database's
/// dialect. Identity values are kept: SQL Server inserts under
/// <c>IDENTITY_INSERT</c>, and PostgreSQL sequences are moved past the loaded
/// keys. Routines and triggers are not included — the native tools
/// (<see cref="NativeTools"/>) dump everything.
/// </summary>
public static class SqlDump
{
    /// <summary>Writes a dump of a database (or one schema).</summary>
    /// <param name="session">A session on the database.</param>
    /// <param name="schemaName">The schema, or <c>null</c> for the provider's default.</param>
    /// <param name="writer">Where the script goes.</param>
    /// <param name="options">What to include.</param>
    /// <param name="log">Receives progress lines.</param>
    /// <param name="cancellationToken">Cancels the dump.</param>
    /// <returns>What was written.</returns>
    public static async Task<SqlDumpSummary> WriteAsync(
        DatabaseSession session,
        string? schemaName,
        TextWriter writer,
        SqlDumpOptions? options = null,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SqlDumpOptions();
        var provider = session.Provider;
        schemaName ??= provider == DatabaseProvider.MySql ? session.Database : null;
        log?.Report("Reading the schema…");
        var full = await session.Introspector.IntrospectAsync(schemaName, cancellationToken).ConfigureAwait(false);
        var tables = full.Tables.Where(t => options.Tables is null || options.Tables.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var schema = new DatabaseSchema
        {
            Name = full.Name,
            Tables = tables,
            Views = options.Tables is null ? DependencyOrder(full.Views, provider) : [],
            Sequences = options.Tables is null ? full.Sequences : [],
        };

        var generator = AlterScriptGenerator.ForDatabase(provider);
        var sql = generator.Sql;
        var statements = 0;
        void Write(string statement)
        {
            writer.WriteLine(statement);
            statements++;
        }

        writer.WriteLine($"-- Bengine SQL dump of {session.Database} ({provider}), {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"-- {tables.Count} table(s); schema: {(options.Schema ? "yes" : "no")}, data: {(options.Data ? "yes" : "no")}");
        writer.WriteLine();
        foreach (var line in Prologue(provider))
        {
            Write(line);
        }

        // Pre-data: sequences and tables (SQLite keeps its keys inline; it cannot add them later).
        var changes = SchemaDiffer.Compare(new DatabaseSchema { Name = schema.Name }, schema, new SchemaDiffOptions { Provider = provider });
        var postData = new List<SchemaChange>();
        if (options.Schema)
        {
            writer.WriteLine();
            if (provider == DatabaseProvider.Sqlite)
            {
                foreach (var table in tables)
                {
                    Write(sql.CreateTable(table));
                }

                postData.AddRange(changes.Where(c => c is AddIndexChange or CreateViewChange));
            }
            else
            {
                foreach (var statement in generator.Render(changes.Where(c => c is not (AddIndexChange or AddForeignKeyChange or CreateViewChange)).ToList(), null, schema))
                {
                    Write(statement);
                }

                postData.AddRange(changes.Where(c => c is AddIndexChange));
                postData.AddRange(changes.Where(c => c is AddForeignKeyChange));
                postData.AddRange(changes.Where(c => c is CreateViewChange));
            }
        }

        long rows = 0;
        if (options.Data)
        {
            foreach (var table in ParentsFirst(tables))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await WriteRowsAsync(session, sql, table, writer, Math.Clamp(options.RowsPerInsert, 1, 1000), cancellationToken).ConfigureAwait(false);
                statements += (int)((count + options.RowsPerInsert - 1) / Math.Max(1, options.RowsPerInsert));
                rows += count;
                log?.Report($"{sql.Qualify(table.Schema, table.Name)}: {count:N0} row(s)");
            }
        }

        if (postData.Count > 0)
        {
            writer.WriteLine();
            foreach (var statement in generator.Render(postData, null, schema))
            {
                // A SQL Server view must be alone in its batch.
                Write(provider == DatabaseProvider.SqlServer && statement.StartsWith("CREATE VIEW", StringComparison.OrdinalIgnoreCase)
                    ? $"GO\n{statement}\nGO"
                    : statement);
            }
        }

        if (options.Data && provider == DatabaseProvider.Oracle)
        {
            // Identities continue after the loaded keys (Oracle 18c and later).
            foreach (var table in tables)
            {
                foreach (var column in table.Columns.Where(c => c.Identity.Strategy != IdentityStrategy.None))
                {
                    Write($"ALTER TABLE {sql.Qualify(table.Schema, table.Name)} MODIFY ({sql.Quote(column.Name)} GENERATED BY DEFAULT AS IDENTITY (START WITH LIMIT VALUE));");
                }
            }
        }

        if (options.Data && provider == DatabaseProvider.PostgreSql)
        {
            // Identity and serial sequences continue after the loaded keys.
            foreach (var table in tables)
            {
                foreach (var column in table.Columns.Where(c => c.Identity.Strategy != IdentityStrategy.None))
                {
                    var name = sql.Qualify(table.Schema, table.Name);
                    Write($"SELECT setval(pg_get_serial_sequence('{name.Replace("'", "''", StringComparison.Ordinal)}', '{column.Name.Replace("'", "''", StringComparison.Ordinal)}'), COALESCE(MAX({sql.Quote(column.Name)}), 0) + 1, false) FROM {name};");
                }
            }
        }

        writer.WriteLine();
        foreach (var line in Epilogue(provider))
        {
            Write(line);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        log?.Report($"Done: {tables.Count} table(s), {rows:N0} row(s).");
        return new SqlDumpSummary(tables.Count, rows, statements);
    }

    private static async Task<long> WriteRowsAsync(DatabaseSession session, Generators.Sql.SqlGeneratorBase sql, Table table, TextWriter writer, int perInsert, CancellationToken cancellationToken)
    {
        var provider = session.Provider;
        var columns = table.Columns.Where(c => c.Generated is null).ToList();
        if (columns.Count == 0)
        {
            return 0;
        }

        var name = sql.Qualify(table.Schema, table.Name);
        var list = string.Join(", ", columns.Select(c => sql.Quote(c.Name)));
        var identityInsert = provider == DatabaseProvider.SqlServer && columns.Any(c => c.Identity.Strategy != IdentityStrategy.None);
        var order = table.PrimaryKey is { ColumnNames.Count: > 0 } pk ? $" ORDER BY {sql.QuoteList(pk.ColumnNames)}" : string.Empty;
        long count = 0;
        var batch = new StringBuilder();
        var inBatch = 0;
        void Flush()
        {
            if (inBatch == 0)
            {
                return;
            }

            writer.Write(batch.Append(provider == DatabaseProvider.Oracle ? "\nSELECT 1 FROM dual;\n" : ";\n").ToString());
            batch.Clear();
            inBatch = 0;
        }

        await foreach (var row in session.Query.StreamAsync($"SELECT {list} FROM {name}{order}", cancellationToken).ConfigureAwait(false))
        {
            if (count == 0)
            {
                writer.WriteLine();
                writer.WriteLine($"-- Data of {name}");
                if (identityInsert)
                {
                    writer.WriteLine($"SET IDENTITY_INSERT {name} ON;");
                }
            }

            // Oracle before 23ai has no multi-row VALUES: INSERT ALL INTO … SELECT 1 FROM dual.
            batch.Append(provider == DatabaseProvider.Oracle
                ? (inBatch == 0 ? "INSERT ALL\n" : "\n") + $"  INTO {name} ({list}) VALUES ("
                : inBatch == 0 ? $"INSERT INTO {name} ({list}) VALUES\n  (" : ",\n  (");
            for (var i = 0; i < row.Length; i++)
            {
                batch.Append(i == 0 ? string.Empty : ", ").Append(SqlLiterals.Format(row[i], provider));
            }

            batch.Append(')');
            count++;
            if (++inBatch == perInsert)
            {
                Flush();
            }
        }

        Flush();
        if (count > 0 && identityInsert)
        {
            writer.WriteLine($"SET IDENTITY_INSERT {name} OFF;");
        }

        return count;
    }

    // Rows of referenced tables come first, so keys hold even where checks cannot be turned off.
    private static IEnumerable<Table> ParentsFirst(IReadOnlyList<Table> tables)
    {
        var pending = tables.ToList();
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(t => !t.ForeignKeys.Any(fk =>
                !fk.ReferencedTable.Equals(t.Name, StringComparison.OrdinalIgnoreCase)
                && pending.Any(p => p.Name.Equals(fk.ReferencedTable, StringComparison.OrdinalIgnoreCase)))) ?? pending[0];
            pending.Remove(ready);
            yield return ready;
        }
    }

    // Views that select from other views are created after them.
    private static IReadOnlyList<View> DependencyOrder(IReadOnlyList<View> views, DatabaseProvider provider)
    {
        var uses = views.ToDictionary(
            v => v,
            v => Sql.SqlLexer.Lex(v.Definition, provider).Where(l => l.IsName).Select(l => l.Identifier).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var ordered = new List<View>();
        var pending = views.ToList();
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(v => !pending.Any(other => other != v && uses[v].Contains(other.Name))) ?? pending[0];
            ordered.Add(ready);
            pending.Remove(ready);
        }

        return ordered;
    }

    private static IEnumerable<string> Prologue(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.MySql => ["SET NAMES utf8mb4;", "SET FOREIGN_KEY_CHECKS = 0;"],
        DatabaseProvider.Sqlite => ["PRAGMA foreign_keys = OFF;", "PRAGMA defer_foreign_keys = ON;"],
        _ => [],
    };

    private static IEnumerable<string> Epilogue(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.MySql => ["SET FOREIGN_KEY_CHECKS = 1;"],
        DatabaseProvider.Sqlite => ["PRAGMA foreign_keys = ON;"],
        _ => [],
    };
}
