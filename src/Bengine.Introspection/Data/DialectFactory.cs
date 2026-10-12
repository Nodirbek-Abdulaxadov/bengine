namespace Bengine.Introspection.Data;

using Bengine.Enums;
using Bengine.Generators.Sql;

/// <summary>
/// Maps a <see cref="DatabaseProvider"/> to the matching <see cref="SqlDialect"/>
/// so the introspection layer can reuse the generators' identifier quoting
/// rather than re-encoding each provider's delimiters.
/// </summary>
internal static class DialectFactory
{
    /// <summary>Gets the dialect for the provider.</summary>
    /// <param name="provider">The provider whose dialect to return.</param>
    /// <returns>The matching dialect.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The provider is not recognized.</exception>
    public static SqlDialect For(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.PostgreSql => new PostgreSqlDialect(),
        DatabaseProvider.SqlServer => new SqlServerDialect(),
        DatabaseProvider.MySql => new MySqlDialect(),
        DatabaseProvider.Sqlite => new SqliteDialect(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider."),
    };
}
