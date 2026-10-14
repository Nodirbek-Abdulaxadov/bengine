namespace Bengine.Introspection.Activity;

using System.Globalization;
using Bengine.Enums;
using Bengine.Introspection.Model;

/// <summary>One connection to the server, as its activity view shows it.</summary>
public sealed record ActivitySession
{
    /// <summary>Gets the server's id for the session (PostgreSQL pid, MySQL thread id, SQL Server spid).</summary>
    public required string Id { get; init; }

    /// <summary>Gets the login.</summary>
    public string? User { get; init; }

    /// <summary>Gets the database.</summary>
    public string? Database { get; init; }

    /// <summary>Gets the client address.</summary>
    public string? Client { get; init; }

    /// <summary>Gets the application name.</summary>
    public string? Application { get; init; }

    /// <summary>Gets the state (active, idle, idle in transaction, sleeping…).</summary>
    public string? State { get; init; }

    /// <summary>Gets how long the current statement (or state) has lasted, in seconds.</summary>
    public double? Seconds { get; init; }

    /// <summary>Gets what the session waits on, if anything.</summary>
    public string? Wait { get; init; }

    /// <summary>Gets the current or last statement.</summary>
    public string? Query { get; init; }

    /// <summary>Gets the sessions blocking this one.</summary>
    public string? BlockedBy { get; init; }

    /// <summary>Gets whether this is the session reading the activity.</summary>
    public bool IsSelf { get; init; }

    /// <summary>Gets whether the session is doing nothing.</summary>
    public bool IsIdle => State is null || State.StartsWith("idle", StringComparison.OrdinalIgnoreCase) && !State.Contains("transaction", StringComparison.OrdinalIgnoreCase)
        || State.Equals("Sleep", StringComparison.OrdinalIgnoreCase) || State.Equals("sleeping", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One lock held or awaited.</summary>
/// <param name="Session">The session's id.</param>
/// <param name="Object">The locked object.</param>
/// <param name="Mode">The lock mode.</param>
/// <param name="Granted">Whether the lock is held (otherwise awaited).</param>
public sealed record ActivityLock(string Session, string? Object, string Mode, bool Granted);

/// <summary>
/// The server activity a DBA watches: sessions with their state, duration,
/// waits, blockers, and statements, the locks they hold or await, and the
/// actions to cancel a running statement or end a session — for PostgreSQL
/// (<c>pg_stat_activity</c>, <c>pg_locks</c>), MySQL and MariaDB (process
/// list, <c>performance_schema.data_locks</c>), and SQL Server
/// (<c>sys.dm_exec_sessions</c>/<c>requests</c>, <c>sys.dm_tran_locks</c>).
/// SQLite has no server, so it has no activity.
/// </summary>
public static class ServerActivity
{
    /// <summary>Gets whether a provider has server activity.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns><c>true</c> unless SQLite.</returns>
    public static bool IsSupported(DatabaseProvider provider) => provider != DatabaseProvider.Sqlite;

    /// <summary>Lists the sessions.</summary>
    /// <param name="session">A session on the server.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The sessions, longest-running first.</returns>
    public static async Task<IReadOnlyList<ActivitySession>> SessionsAsync(DatabaseSession session, CancellationToken cancellationToken = default)
    {
        var sql = session.Provider switch
        {
            DatabaseProvider.PostgreSql => """
                SELECT pid::text, usename, datname, coalesce(client_addr::text, 'local'), application_name, state,
                       extract(epoch FROM (now() - coalesce(query_start, backend_start)))::float8,
                       CASE WHEN wait_event IS NULL THEN NULL ELSE wait_event_type || ': ' || wait_event END,
                       query, array_to_string(pg_blocking_pids(pid), ', '), pid = pg_backend_pid()
                FROM pg_stat_activity WHERE backend_type = 'client backend'
                """,
            DatabaseProvider.MySql => """
                SELECT CAST(id AS CHAR), user, db, host, NULL, CASE WHEN command = 'Sleep' THEN 'Sleep' ELSE COALESCE(state, command) END,
                       CAST(time AS DOUBLE), NULL, info, NULL, id = CONNECTION_ID()
                FROM information_schema.processlist WHERE command <> 'Daemon'
                """,
            DatabaseProvider.SqlServer => """
                SELECT CAST(s.session_id AS varchar(10)), s.login_name, DB_NAME(s.database_id), s.host_name, s.program_name,
                       COALESCE(r.status, s.status),
                       CAST(DATEDIFF(millisecond, COALESCE(r.start_time, s.last_request_start_time), SYSDATETIME()) / 1000.0 AS float),
                       r.wait_type, t.text, NULLIF(CAST(r.blocking_session_id AS varchar(10)), '0'),
                       CAST(CASE WHEN s.session_id = @@SPID THEN 1 ELSE 0 END AS bit)
                FROM sys.dm_exec_sessions s
                LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
                OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
                WHERE s.is_user_process = 1
                """,
            _ => null,
        };
        if (sql is null)
        {
            return [];
        }

        var result = await session.Query.ExecuteAsync(sql, 5000, cancellationToken).ConfigureAwait(false);
        var blockers = session.Provider == DatabaseProvider.MySql ? await MySqlBlockersAsync(session, cancellationToken).ConfigureAwait(false) : [];
        return result.Rows.Select(r => new ActivitySession
        {
            Id = Text(r[0]) ?? "?",
            User = Text(r[1]),
            Database = Text(r[2]),
            Client = Text(r[3]),
            Application = Text(r[4]),
            State = Text(r[5]),
            Seconds = double.TryParse(Text(r[6]), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? Math.Max(0, seconds) : null,
            Wait = Text(r[7]),
            Query = Text(r[8]),
            BlockedBy = Text(r[9]) is { Length: > 0 } blocked ? blocked : blockers.GetValueOrDefault(Text(r[0]) ?? string.Empty),
            IsSelf = r[10] is true or 1 or 1L or "t" or "1" || (r[10] is bool b && b),
        })
        .OrderBy(s => s.IsIdle)
        .ThenByDescending(s => s.Seconds ?? 0)
        .ToList();
    }

    // MySQL's process list does not say who blocks whom; the lock-wait views do.
    private static async Task<Dictionary<string, string>> MySqlBlockersAsync(DatabaseSession session, CancellationToken cancellationToken)
    {
        string[] queries =
        [
            """
            SELECT CAST(r.processlist_id AS CHAR), CAST(b.processlist_id AS CHAR)
            FROM performance_schema.data_lock_waits w
            JOIN performance_schema.threads r ON r.thread_id = w.requesting_thread_id
            JOIN performance_schema.threads b ON b.thread_id = w.blocking_thread_id
            """,
            """
            SELECT CAST(r.trx_mysql_thread_id AS CHAR), CAST(b.trx_mysql_thread_id AS CHAR)
            FROM information_schema.innodb_lock_waits w
            JOIN information_schema.innodb_trx r ON r.trx_id = w.requesting_trx_id
            JOIN information_schema.innodb_trx b ON b.trx_id = w.blocking_trx_id
            """,
        ];
        foreach (var query in queries)
        {
            try
            {
                var rows = await session.Query.ExecuteAsync(query, 5000, cancellationToken).ConfigureAwait(false);
                return rows.Rows
                    .GroupBy(r => Text(r[0]) ?? string.Empty)
                    .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(r => Text(r[1])).Distinct()));
            }
            catch (IntrospectionException)
            {
                // Try the next server flavor's view.
            }
        }

        return [];
    }

    /// <summary>Lists locks (granted and awaited), on relations where the server says which.</summary>
    /// <param name="session">A session on the server.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The locks, awaited ones first.</returns>
    public static async Task<IReadOnlyList<ActivityLock>> LocksAsync(DatabaseSession session, CancellationToken cancellationToken = default)
    {
        var sql = session.Provider switch
        {
            DatabaseProvider.PostgreSql => """
                SELECT l.pid::text, coalesce(n.nspname || '.' || c.relname, l.locktype || coalesce(' ' || l.transactionid::text, '')), l.mode, l.granted
                FROM pg_locks l LEFT JOIN pg_class c ON c.oid = l.relation LEFT JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE l.pid <> pg_backend_pid() AND (c.oid IS NULL OR n.nspname NOT IN ('pg_catalog', 'information_schema'))
                """,
            DatabaseProvider.MySql => """
                SELECT CAST(t.processlist_id AS CHAR), CONCAT(l.object_schema, '.', l.object_name, COALESCE(CONCAT(' (', l.index_name, ')'), '')),
                       CONCAT(l.lock_type, ' ', l.lock_mode), l.lock_status = 'GRANTED'
                FROM performance_schema.data_locks l JOIN performance_schema.threads t ON t.thread_id = l.thread_id
                """,
            DatabaseProvider.SqlServer => """
                SELECT CAST(l.request_session_id AS varchar(10)),
                       CASE l.resource_type WHEN 'OBJECT' THEN OBJECT_SCHEMA_NAME(l.resource_associated_entity_id) + '.' + OBJECT_NAME(l.resource_associated_entity_id)
                            ELSE l.resource_type + ' ' + l.resource_description END,
                       l.request_mode, CAST(CASE WHEN l.request_status = 'GRANT' THEN 1 ELSE 0 END AS bit)
                FROM sys.dm_tran_locks l WHERE l.request_session_id <> @@SPID AND l.resource_type <> 'DATABASE'
                """,
            _ => null,
        };
        if (sql is null)
        {
            return [];
        }

        try
        {
            var result = await session.Query.ExecuteAsync(sql, 5000, cancellationToken).ConfigureAwait(false);
            return result.Rows
                .Select(r => new ActivityLock(Text(r[0]) ?? "?", Text(r[1]), Text(r[2]) ?? string.Empty, r[3] is true or 1 or 1L or "t" || (r[3] is bool b && b)))
                .OrderBy(l => l.Granted)
                .ToList();
        }
        catch (IntrospectionException) when (session.Provider == DatabaseProvider.MySql)
        {
            // MariaDB and old MySQL have no performance_schema.data_locks.
            return [];
        }
    }

    /// <summary>Cancels the statement a session is running, keeping the session.</summary>
    /// <param name="session">A session on the server.</param>
    /// <param name="id">The target session's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the server accepted the request.</returns>
    public static Task CancelAsync(DatabaseSession session, string id, CancellationToken cancellationToken = default) =>
        session.Query.ExecuteAllAsync(session.Provider switch
        {
            DatabaseProvider.PostgreSql => $"SELECT pg_cancel_backend({Number(id)})",
            DatabaseProvider.MySql => $"KILL QUERY {Number(id)}",
            DatabaseProvider.SqlServer => $"KILL {Number(id)}",
            _ => throw new IntrospectionException("SQLite has no server sessions."),
        }, 1, cancellationToken);

    /// <summary>Ends a session (its open transaction is rolled back).</summary>
    /// <param name="session">A session on the server.</param>
    /// <param name="id">The target session's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the server accepted the request.</returns>
    public static Task TerminateAsync(DatabaseSession session, string id, CancellationToken cancellationToken = default) =>
        session.Query.ExecuteAllAsync(session.Provider switch
        {
            DatabaseProvider.PostgreSql => $"SELECT pg_terminate_backend({Number(id)})",
            DatabaseProvider.MySql or DatabaseProvider.SqlServer => $"KILL {Number(id)}",
            _ => throw new IntrospectionException("SQLite has no server sessions."),
        }, 1, cancellationToken);

    // Session ids are integers; anything else is refused rather than spliced into SQL.
    private static long Number(string id) =>
        long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : throw new IntrospectionException($"'{id}' is not a session id.");

    private static string? Text(object? value) => value switch
    {
        null => null,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
