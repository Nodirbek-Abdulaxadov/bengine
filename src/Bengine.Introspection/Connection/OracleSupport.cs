namespace Bengine.Introspection.Connection;

using System.Data.Common;
using Oracle.ManagedDataAccess.Client;

/// <summary>
/// What ODP.NET needs to behave like the other drivers here: parameters bound
/// by name (<c>:name</c> in SQL, <c>name</c> as the parameter), <c>LONG</c>
/// columns (view text, column defaults) and LOBs read whole, NUMBER values too
/// big for <see cref="decimal"/> truncated instead of throwing, and a session
/// that reads and writes dates and numbers in ISO form, so values typed as
/// text (filters, edits, scripts) convert whatever the server's NLS settings.
/// </summary>
internal static class OracleSupport
{
    /// <summary>The session settings applied to every Oracle connection.</summary>
    internal const string SessionSettings =
        "ALTER SESSION SET NLS_DATE_FORMAT = 'YYYY-MM-DD HH24:MI:SS' " +
        "NLS_TIMESTAMP_FORMAT = 'YYYY-MM-DD HH24:MI:SS.FF' " +
        "NLS_TIMESTAMP_TZ_FORMAT = 'YYYY-MM-DD HH24:MI:SS.FF TZH:TZM' " +
        "NLS_NUMERIC_CHARACTERS = '.,'";

    /// <summary>Prepares a command (no-op for other drivers).</summary>
    /// <param name="command">The command.</param>
    public static void Prepare(DbCommand command)
    {
        if (command is OracleCommand oracle)
        {
            oracle.BindByName = true;
            oracle.InitialLONGFetchSize = -1;
            oracle.InitialLOBFetchSize = -1;
        }
    }

    /// <summary>Prepares a reader (no-op for other drivers).</summary>
    /// <param name="reader">The reader.</param>
    public static void Prepare(DbDataReader reader)
    {
        if (reader is OracleDataReader oracle)
        {
            oracle.SuppressGetDecimalInvalidCastException = true;
        }
    }

    /// <summary>
    /// Reads a value exactly: an Oracle NUMBER comes back as a <see cref="decimal"/>
    /// when it fits, else as its full text (ODP.NET would otherwise round through
    /// <see cref="double"/> or truncate); other values pass through.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">The column.</param>
    /// <returns>The value, or <see cref="DBNull.Value"/>.</returns>
    public static object GetValue(DbDataReader reader, int ordinal)
    {
        if (reader is OracleDataReader oracle && oracle.GetProviderSpecificFieldType(ordinal) == typeof(Oracle.ManagedDataAccess.Types.OracleDecimal))
        {
            var number = oracle.GetOracleDecimal(ordinal);
            if (number.IsNull)
            {
                return DBNull.Value;
            }

            var text = number.ToString();
            return decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var exact)
                && exact.ToString(System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.') == text.TrimEnd('0').TrimEnd('.')
                ? (exact == decimal.Truncate(exact) && exact >= long.MinValue && exact <= long.MaxValue ? (object)(long)exact : exact)
                : text;
        }

        return reader.GetValue(ordinal);
    }

    /// <summary>Gets a parameter's name as the driver wants it (ODP.NET takes it without the <c>:</c>).</summary>
    /// <param name="command">The command.</param>
    /// <param name="name">The name as written in the SQL.</param>
    /// <returns>The parameter name.</returns>
    public static string ParameterName(DbCommand command, string name) =>
        command is OracleCommand ? name.TrimStart('@', ':') : name;

    /// <summary>Applies the session settings to a newly opened connection.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>A task that completes when applied.</returns>
    public static async Task InitializeAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not OracleConnection)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = SessionSettings;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
