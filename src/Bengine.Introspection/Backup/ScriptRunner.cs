namespace Bengine.Introspection.Backup;

using Bengine.Introspection.Sql;

/// <summary>Progress of a script run.</summary>
/// <param name="Done">Statements run so far.</param>
/// <param name="Total">Statements in the script.</param>
/// <param name="Failed">Statements that failed (when not stopping on errors).</param>
/// <param name="Message">A line for the log, or <c>null</c>.</param>
public sealed record ScriptProgress(int Done, int Total, int Failed, string? Message);

/// <summary>
/// Runs a SQL script file statement by statement on one session — the
/// "Run SQL Script" restore that works for every provider and any script
/// (a Bengine dump, a <c>pg_dump</c> plain file without <c>COPY</c>, a
/// <c>mysqldump</c> file). Statements are split by the provider's rules
/// (<c>GO</c>, <c>DELIMITER</c>, dollar quotes); the run stops at the first
/// error unless asked to continue, and can run in one transaction.
/// </summary>
public static class ScriptRunner
{
    /// <summary>Runs a script.</summary>
    /// <param name="session">The session.</param>
    /// <param name="script">The script text.</param>
    /// <param name="stopOnError">Whether to stop at the first failing statement.</param>
    /// <param name="singleTransaction">Whether to run everything in one transaction (rolled back on failure).</param>
    /// <param name="progress">Receives progress.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The final progress.</returns>
    /// <exception cref="IntrospectionException">A statement failed and the run stopped.</exception>
    public static async Task<ScriptProgress> RunAsync(
        DatabaseSession session,
        string script,
        bool stopOnError = true,
        bool singleTransaction = false,
        IProgress<ScriptProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var statements = SqlScript.Split(script, session.Provider)
                        // MySQL runs /*! … */ comments (mysqldump's session settings).
            .Where(s => SqlLexer.Lex(s.Text, session.Provider).Any(l => l.IsSignificant
                || (session.Provider == Bengine.Enums.DatabaseProvider.MySql && l.Kind == SqlLexemeKind.Comment && l.Text.StartsWith("/*!", StringComparison.Ordinal))))
            .ToList();
        var failed = 0;
        var report = DateTime.MinValue;
        if (singleTransaction)
        {
            await session.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        try
        {
            for (var i = 0; i < statements.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await session.Query.ExecuteAllAsync(statements[i].Text, 1, cancellationToken).ConfigureAwait(false);
                }
                catch (IntrospectionException ex)
                {
                    var line = script[..statements[i].Start].Count(c => c == '\n') + 1;
                    var message = $"Line {line}: {ex.Message}";
                    if (stopOnError)
                    {
                        progress?.Report(new ScriptProgress(i, statements.Count, failed + 1, message));
                        throw new IntrospectionException(message, ex);
                    }

                    failed++;
                    progress?.Report(new ScriptProgress(i + 1, statements.Count, failed, message));
                    continue;
                }

                if (DateTime.UtcNow - report > TimeSpan.FromMilliseconds(250) || i == statements.Count - 1)
                {
                    report = DateTime.UtcNow;
                    progress?.Report(new ScriptProgress(i + 1, statements.Count, failed, null));
                }
            }

            if (singleTransaction)
            {
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch when (singleTransaction && session.InTransaction)
        {
            await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new ScriptProgress(statements.Count, statements.Count, failed, null);
    }
}
