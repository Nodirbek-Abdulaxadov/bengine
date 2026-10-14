namespace Bengine.Introspection.Design;

using Bengine.Diff;
using Bengine.Enums;
using Bengine.Generators.Migrations;
using Bengine.Models;

/// <summary>
/// Renders and applies schema changes on a live session. Rendering adds what
/// only the database knows — on SQLite, the triggers a table rebuild must
/// replay. Applying runs the statements in one transaction where the provider
/// has transactional DDL (PostgreSQL, SQL Server, SQLite), so a failure leaves
/// the schema untouched; MySQL commits each DDL statement implicitly, so there
/// they run one by one and stop at the first failure. On SQLite, foreign keys
/// are switched off around the transaction (a rebuild drops the old table) and
/// checked afterwards.
/// </summary>
public static class SchemaApplier
{
    /// <summary>Renders changes for the session's provider.</summary>
    /// <param name="session">The session.</param>
    /// <param name="changes">The changes.</param>
    /// <param name="source">The schema the changes start from.</param>
    /// <param name="target">The schema the changes lead to.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The statements.</returns>
    public static async Task<IReadOnlyList<string>> RenderAsync(
        DatabaseSession session,
        IReadOnlyList<SchemaChange> changes,
        DatabaseSchema? source,
        DatabaseSchema? target,
        CancellationToken cancellationToken = default)
    {
        var generator = AlterScriptGenerator.For(session.Provider);
        if (session.Provider == DatabaseProvider.Sqlite)
        {
            var tables = changes.Where(AlterScriptGenerator.NeedsRebuild).Select(c => c.Subject).Distinct().ToList();
            var triggers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables)
            {
                var rows = await session.Query.ExecuteAsync(
                    $"SELECT sql FROM sqlite_master WHERE type = 'trigger' AND tbl_name = '{table.Replace("'", "''", StringComparison.Ordinal)}' AND sql IS NOT NULL",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                triggers[table] = rows.Rows.Select(r => (string)r[0]!).ToList();
            }

            generator.RebuildEpilogue = table => triggers.GetValueOrDefault(table, []);
        }

        return generator.Render(changes, source, target);
    }

    /// <summary>Runs DDL statements, transactionally where the provider allows it.</summary>
    /// <param name="session">The session.</param>
    /// <param name="statements">The statements.</param>
    /// <param name="log">Receives each statement as it runs.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when every statement ran.</returns>
    /// <exception cref="IntrospectionException">A statement failed; transactional providers rolled everything back.</exception>
    public static async Task ApplyAsync(
        DatabaseSession session,
        IReadOnlyList<string> statements,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var work = statements.Where(s => !s.TrimStart().StartsWith("--", StringComparison.Ordinal)).ToList();
        var transactional = session.Provider != DatabaseProvider.MySql && !session.InTransaction;
        var sqlite = session.Provider == DatabaseProvider.Sqlite;
        if (sqlite)
        {
            work = work.Where(s => !s.StartsWith("PRAGMA foreign_keys", StringComparison.OrdinalIgnoreCase)).ToList();
            await session.Query.ExecuteAllAsync("PRAGMA foreign_keys = OFF", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (transactional)
            {
                await session.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            foreach (var statement in work)
            {
                log?.Invoke(statement);
                try
                {
                    await session.Query.ExecuteAllAsync(statement, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (IntrospectionException ex)
                {
                    throw new IntrospectionException($"{ex.Message}\n\nwhile running:\n{statement}", ex);
                }
            }

            if (sqlite)
            {
                var violations = await session.Query.ExecuteAsync("PRAGMA foreign_key_check", cancellationToken: cancellationToken).ConfigureAwait(false);
                if (violations.Rows.Count > 0)
                {
                    throw new IntrospectionException(
                        $"The change would break {violations.Rows.Count} foreign-key reference(s), for example in table {violations.Rows[0][0]}. Nothing was changed.");
                }
            }

            if (transactional)
            {
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (transactional && session.InTransaction)
            {
                await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (sqlite)
            {
                await session.Query.ExecuteAllAsync("PRAGMA foreign_keys = ON", cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
